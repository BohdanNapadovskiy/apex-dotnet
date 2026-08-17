using System.Globalization;
using iText.IO.Font;
using iText.IO.Font.Otf;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using SkiaSharp;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Resolves font/style attributes for a <c>(page, mcid)</c> key by walking the source
/// PDF's content streams via iText 9. Per §5.3 decision option (b), this class
/// treats the source PDF as a font-and-style-only reference — structure and
/// content are still taken exclusively from JSON.
///
/// Processing is lazy on first <see cref="Resolve"/> call, then cached in-memory.
/// The first style seen for a given (page, mcid) wins; later text events with the
/// same key are ignored (in practice all glyphs inside one MCID share the same Tf state).
///
/// <b>Port note</b> — the Java implementation uses an AWT-based glyph-outline rescue
/// path (<c>awtHasOutline</c>) in <c>firstUnrenderableCodePointForOcrRaster</c> to accept
/// characters whose outline exists in the extracted TTF but is missing from the source's
/// PDF encoding cmap. .NET has no AWT — the equivalent here uses <b>SkiaSharp</b>:
/// <see cref="SKTypeface.FromData"/> loads the source's embedded FontFile bytes, then
/// <see cref="SKFont.GetGlyphs"/> returns 0 for chars whose outline isn't in the TTF.
/// </summary>
public sealed class SourcePdfFontResolver : IDisposable
{
    private readonly PdfDocument _pdf;
    private readonly Dictionary<long, FontStyle> _byPageMcid = new();
    private readonly Dictionary<long, PdfFont> _fontByPageMcid = new();

    /// <summary>Text runs (in source order) for each (page, mcid), preserving inline style transitions.</summary>
    private readonly Dictionary<long, List<TextRun>> _runsByPageMcid = new();

    /// <summary>
    /// Characters ever rendered by each source font, keyed by the font dict's
    /// indirect reference. Guaranteed superset of the font's embedded subset glyphs.
    /// </summary>
    private readonly Dictionary<PdfIndirectReference, HashSet<int>> _renderedCharsByFontRef = new();

    /// <summary>
    /// Per-source-font <see cref="SKTypeface"/> extracted from the embedded FontFile
    /// stream. Populated lazily. Cache lifetime = this resolver, keyed by the source
    /// PDF font-dict's indirect reference. Null cached as missing so a wide guard sweep
    /// only extracts once. Disposed in <see cref="Dispose"/>.
    /// </summary>
    private readonly Dictionary<PdfIndirectReference, SKTypeface?> _skiaTypefaceByFontRef = new();

    private bool _processed;
    private bool _disposed;

    public SourcePdfFontResolver(string pdfPath)
    {
        _pdf = new PdfDocument(new PdfReader(pdfPath));
    }

    public FontStyle? Resolve(int page, int mcid)
    {
        if (mcid < 0) return null;
        ProcessIfNeeded();
        return _byPageMcid.TryGetValue(Key(page, mcid), out var v) ? v : null;
    }

    /// <summary>
    /// Source's embedded <see cref="PdfFont"/> for (page, mcid), if the block had a
    /// resolvable font on its first text-render event. This is the exact font subset
    /// the source producer put in the PDF — reusing it on the writer side gives visible
    /// edit output that matches the surrounding scan raster glyph-for-glyph. Null when
    /// the MCID rendered no text with a resolvable font (image-only MCID, or unusual
    /// font dictionaries iText couldn't parse). Callers should gate use on
    /// <see cref="FirstUnrenderableCodePoint"/> first.
    /// </summary>
    public PdfFont? SourceFontFor(int page, int mcid)
    {
        if (mcid < 0) return null;
        ProcessIfNeeded();
        return _fontByPageMcid.TryGetValue(Key(page, mcid), out var v) ? v : null;
    }

    /// <summary>
    /// Source's inline text runs for a given (page, mcid), in source order. Each run's
    /// text is contiguous in source and shares a single <see cref="FontStyle"/>;
    /// transitions between runs mark where source switches font / size / weight / colour
    /// mid-block. Empty list when the MCID had no text render events.
    /// </summary>
    public IReadOnlyList<TextRun> ResolveRuns(int page, int mcid)
    {
        if (mcid < 0) return Array.Empty<TextRun>();
        ProcessIfNeeded();
        if (!_runsByPageMcid.TryGetValue(Key(page, mcid), out var runs) || runs is null)
        {
            return Array.Empty<TextRun>();
        }
        // Backing list is stable post-ProcessIfNeeded; expose it via the read-only view.
        return runs;
    }

    /// <summary>
    /// Missing character (as a Unicode code point) that the source's embedded font for
    /// (page, mcid) cannot render from <paramref name="text"/>, or null if every character
    /// is covered. Returns null for MCIDs where no font was resolved (no restriction —
    /// caller can proceed) and for empty/null text.
    ///
    /// Used by <c>EditEngine</c> as a setText pre-flight: refusing an edit whose new
    /// content needs glyphs outside the source's embedded subset avoids the writer's
    /// universal fallback path, which trips PAC 2024's cross-page checks.
    /// </summary>
    public int? FirstUnrenderableCodePoint(int page, int mcid, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (mcid < 0) return null;
        ProcessIfNeeded();
        if (!_fontByPageMcid.TryGetValue(Key(page, mcid), out var font) || font is null) return null;
        int cmapMiss = PageFontInventory.FirstMissingCodePoint(font, text);
        if (cmapMiss >= 0) return cmapMiss;

        // A char is safe to setText only if BOTH:
        //  (1) the source font's outline data ACTUALLY has a glyph for it — observed
        //      via TextRenderInfo.GetText() → /ToUnicode-decoded chars we saw on the page.
        //  (2) the font can ENCODE the char to a CID for output — via ContainsGlyph on
        //      the encoding cmap.
        // Failing either check pushes the writer down the universal-fallback path,
        // which PAC 2024 catches as "Components required, but found N".
        var renderedChars = FontRenderedChars(font);
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (!IsSkippableWhitespace(cp))
            {
                bool inRendered = renderedChars is null || renderedChars.Contains(cp);
                bool inEncoding = font.ContainsGlyph(cp);
                Glyph? g = inEncoding ? font.GetGlyph(cp) : null;
                bool hasOutline = g is not null && g.GetCode() > 0;
                if (!inRendered || !inEncoding || !hasOutline) return cp;
            }
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
        return null;
    }

    /// <summary>
    /// OCR-raster-aware variant of <see cref="FirstUnrenderableCodePoint"/>. Same strict
    /// iText-encoding check first, but before flagging a code point, tries to rescue it
    /// by asking the SkiaSharp <see cref="SKTypeface"/> loaded from the source PDF's
    /// embedded FontFile stream. If the extracted TTF has a real glyph for the character,
    /// the OCR raster stamper CAN draw it — iText's "no" only means the PDF encoding
    /// cmap doesn't map it, which is irrelevant when we're painting a PNG.
    ///
    /// Use for OCR route pre-flight where the writer will raster-stamp new content over
    /// a redaction rectangle. The strict variant stays for the native (source-based)
    /// writer because vector-mode text output really does fall through to Arial when
    /// the source encoding lacks the code point.
    /// </summary>
    public int? FirstUnrenderableCodePointForOcrRaster(int page, int mcid, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (mcid < 0) return null;
        ProcessIfNeeded();
        if (!_fontByPageMcid.TryGetValue(Key(page, mcid), out var font) || font is null) return null;
        var renderedChars = FontRenderedChars(font);
        var typeface = SkiaTypefaceFor(font);
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (!IsSkippableWhitespace(cp))
            {
                bool inRendered = renderedChars is null || renderedChars.Contains(cp);
                bool inEncoding = font.ContainsGlyph(cp);
                Glyph? g = inEncoding ? font.GetGlyph(cp) : null;
                bool hasItextOutline = g is not null && g.GetCode() > 0;
                if (!inRendered || !inEncoding || !hasItextOutline)
                {
                    // iText encoding fails — rescue via SkiaSharp outline. When Skia
                    // finds a real glyph for cp we know the raster stamper's
                    // StampOntoWithFont will draw a real glyph, so there's no .notdef
                    // risk in the output.
                    if (typeface is null || !SkiaHasGlyph(typeface, cp))
                    {
                        return cp;
                    }
                }
            }
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
        return null;
    }

    internal SKTypeface? SkiaTypefaceFor(PdfFont font)
    {
        var obj = font.GetPdfObject();
        if (obj is null) return null;
        var refr = obj.GetIndirectReference();
        if (refr is null) return null;
        if (_skiaTypefaceByFontRef.TryGetValue(refr, out var cached)) return cached;
        var result = ExtractSkiaTypeface(obj);
        _skiaTypefaceByFontRef[refr] = result;
        return result;
    }

    private static SKTypeface? ExtractSkiaTypeface(PdfObject fontObj)
    {
        if (fontObj is not PdfDictionary fontDict) return null;
        var descriptor = fontDict.GetAsDictionary(PdfName.FontDescriptor);
        if (descriptor is null)
        {
            var descendants = fontDict.GetAsArray(PdfName.DescendantFonts);
            if (descendants is not null && descendants.Size() > 0)
            {
                var d0 = descendants.GetAsDictionary(0);
                if (d0 is not null) descriptor = d0.GetAsDictionary(PdfName.FontDescriptor);
            }
        }
        if (descriptor is null) return null;
        var stream = descriptor.GetAsStream(PdfName.FontFile2)
                     ?? descriptor.GetAsStream(PdfName.FontFile3)
                     ?? descriptor.GetAsStream(PdfName.FontFile);
        if (stream is null) return null;
        try
        {
            var bytes = stream.GetBytes();
            if (bytes is null || bytes.Length == 0) return null;
            var data = SKData.CreateCopy(bytes);
            return SKTypeface.FromData(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reliable "does Skia actually have a drawable glyph for cp?" check. Uses
    /// <see cref="SKFont.GetGlyphs"/> — glyph ID 0 = .notdef. Skia's cmap lookup is
    /// backed by the actual outline data (unlike iText's ContainsGlyph, which trusts
    /// the encoding cmap even when the outline is stripped from a subset).
    /// </summary>
    private static bool SkiaHasGlyph(SKTypeface typeface, int cp)
    {
        using var font = new SKFont(typeface);
        ReadOnlySpan<int> cps = stackalloc int[] { cp };
        Span<ushort> glyphs = stackalloc ushort[1];
        font.GetGlyphs(cps, glyphs);
        return glyphs[0] != 0;
    }

    private HashSet<int>? FontRenderedChars(PdfFont font)
    {
        var obj = font.GetPdfObject();
        if (obj is null) return null;
        var refr = obj.GetIndirectReference();
        if (refr is null) return null;
        return _renderedCharsByFontRef.TryGetValue(refr, out var v) ? v : null;
    }

    private static bool IsSkippableWhitespace(int cp) => cp == '\n' || cp == '\r' || cp == '\t';

    public int KnownEntryCount()
    {
        ProcessIfNeeded();
        return _byPageMcid.Count;
    }

    /// <summary>
    /// Shared handle on the source PDF for writers that need to lift resources
    /// from it (page size, image XObjects for OCR raster passthrough). Reuses the
    /// reader this resolver already opened — opening a second one would race
    /// Windows' file lock.
    /// </summary>
    public PdfDocument SourceDocument => _pdf;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, typeface) in _skiaTypefaceByFontRef)
        {
            typeface?.Dispose();
        }
        _skiaTypefaceByFontRef.Clear();
        _pdf.Close();
    }

    private void ProcessIfNeeded()
    {
        if (_processed) return;
        int pageCount = _pdf.GetNumberOfPages();
        for (int p = 1; p <= pageCount; p++)
        {
            var listener = new RenderListener(this, p);
            var processor = new PdfCanvasProcessor(listener);
            processor.ProcessPageContent(_pdf.GetPage(p));
        }
        _processed = true;
    }

    private sealed class RenderListener : IEventListener
    {
        private static readonly ICollection<EventType> Supported =
            new HashSet<EventType> { EventType.RENDER_TEXT };

        private readonly SourcePdfFontResolver _owner;
        private readonly int _page;

        internal RenderListener(SourcePdfFontResolver owner, int page)
        {
            _owner = owner;
            _page = page;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            if (data is not TextRenderInfo tri) return;
            var f = tri.GetFont();
            if (f is not null) _owner.RecordRenderedChars(f, tri.GetText());
            int mcid = tri.GetMcid();
            if (mcid < 0) return;
            long k = Key(_page, mcid);
            // First TRI of an MCID sets its primary (font-lookup) style + font
            // for the glyph-coverage guard. Subsequent TRIs contribute to the
            // run list only — they don't overwrite the primary.
            if (!_owner._byPageMcid.ContainsKey(k))
            {
                _owner._byPageMcid[k] = StyleOf(tri);
                if (f is not null) _owner._fontByPageMcid[k] = f;
            }
            else
            {
                // In-block TL upgrade: the first tri captures whatever TL was
                // in effect on BDC entry, which may be leaked from an earlier
                // block's TD. Any later tri inside the same MCID that shows a
                // DIFFERENT valid TL is source's real intent — prefer it over
                // the inherited value.
                var prev = _owner._byPageMcid[k];
                float lr = LeadingRatio(tri);
                if (lr > 0f && Math.Abs(lr - prev.LeadingRatio) > 0.001f)
                {
                    _owner._byPageMcid[k] = new FontStyle(
                        prev.Family, prev.Size, prev.Weight,
                        prev.ColorHex, prev.SourceFontObjNumber, lr);
                }
            }
            // Accumulate inline runs — group consecutive TRIs sharing a style.
            var text = tri.GetText();
            if (string.IsNullOrEmpty(text)) return;
            var style = StyleOf(tri);
            if (!_owner._runsByPageMcid.TryGetValue(k, out var runs) || runs is null)
            {
                runs = new List<TextRun>();
                _owner._runsByPageMcid[k] = runs;
            }
            if (runs.Count > 0 && runs[^1].Style.Equals(style))
            {
                var prevRun = runs[^1];
                runs[^1] = new TextRun(prevRun.Text + text, style);
            }
            else
            {
                runs.Add(new TextRun(text, style));
            }
        }

        public ICollection<EventType> GetSupportedEvents() => Supported;
    }

    /// <summary>
    /// Track every character seen rendered by <paramref name="font"/> so
    /// <see cref="FirstUnrenderableCodePoint"/> can distinguish declared coverage from
    /// actually-embedded glyph outlines.
    /// </summary>
    private void RecordRenderedChars(PdfFont font, string? text)
    {
        if (font is null || string.IsNullOrEmpty(text)) return;
        var obj = font.GetPdfObject();
        if (obj is null) return;
        var refr = obj.GetIndirectReference();
        if (refr is null) return;
        if (!_renderedCharsByFontRef.TryGetValue(refr, out var chars) || chars is null)
        {
            chars = new HashSet<int>();
            _renderedCharsByFontRef[refr] = chars;
        }
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            chars.Add(cp);
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
    }

    private static FontStyle StyleOf(TextRenderInfo tri)
    {
        var font = tri.GetFont();
        string? family = null;
        string weight = "regular";
        int? fontObjNumber = null;
        if (font is not null)
        {
            var program = font.GetFontProgram();
            if (program is not null)
            {
                var names = program.GetFontNames();
                family = names?.GetFontName();
                weight = DetectWeight(names);
            }
            // Capture the source font resource's PDF object number so the writer can
            // pick THIS specific font (Type0/simple twins of the same BaseFont hit this).
            var obj = font.GetPdfObject();
            var refr = obj?.GetIndirectReference();
            if (refr is not null) fontObjNumber = refr.GetObjNumber();
        }
        return new FontStyle(family, EffectiveFontSize(tri), weight,
            HexColor(tri.GetFillColor()), fontObjNumber, LeadingRatio(tri));
    }

    /// <summary>
    /// Ratio of TL / Tf in source text-space. Dimensionless — writers multiply by
    /// their emitted (effective) font size to reproduce source's inter-line spacing.
    /// Returns 0 when either component is missing / non-positive, or when the ratio
    /// falls outside a sane [0.9, 2.0] band.
    /// </summary>
    private static float LeadingRatio(TextRenderInfo tri)
    {
        float tf = tri.GetFontSize();
        if (!(tf > 0)) return 0f;
        float tl = tri.GetLeading();
        if (!(tl > 0)) return 0f;
        float ratio = tl / tf;
        if (ratio < 0.9f || ratio > 2.0f) return 0f;
        return ratio;
    }

    /// <summary>
    /// Return an effective (rendered) font size in user-space points. Many producers set
    /// Tf to size 1 and scale the text matrix instead — in that case GetFontSize returns 1.
    ///
    /// Recovery order: Tm.d direct read → width-measurement of the actual baseline →
    /// ascent-descent height → raw. First pass to yield a size in [4, 200] wins.
    /// </summary>
    private static float EffectiveFontSize(TextRenderInfo tri)
    {
        float raw = tri.GetFontSize();
        if (raw >= 4.0f) return raw;
        try
        {
            var tm = tri.GetTextMatrix();
            if (tm is not null && raw > 0)
            {
                float d = Math.Abs(tm.Get(Matrix.I22));
                float effective = raw * d;
                if (effective >= 4.0f && effective <= 200.0f) return effective;
            }
        }
        catch
        {
            // Fall through to width-measurement recovery.
        }
        try
        {
            var text = tri.GetText();
            var font = tri.GetFont();
            if (font is not null && !string.IsNullOrEmpty(text))
            {
                float designedWidth = font.GetWidth(text, 1.0f);
                float actualWidth = tri.GetBaseline().GetLength();
                if (designedWidth > 0.0f && actualWidth > 0.0f)
                {
                    float scale = actualWidth / designedWidth;
                    if (scale >= 4.0f) return scale;
                }
            }
        }
        catch
        {
            // Fall through to ascent-descent fallback.
        }
        try
        {
            var ascent = tri.GetAscentLine();
            var descent = tri.GetDescentLine();
            float height = Math.Abs(
                ascent.GetStartPoint().Get(1) - descent.GetStartPoint().Get(1));
            if (height >= 4.0f) return height;
        }
        catch
        {
            // Fall through to raw.
        }
        return raw;
    }

    /// <summary>
    /// Multi-source weight detection. iText's <c>FontNames.GetFontWeight()</c> alone
    /// misses bold on subsetted OCR fonts — this cross-checks OS/2 fsSelection, macStyle,
    /// and every name string before falling back to <see cref="WeightName"/>.
    /// </summary>
    private static string DetectWeight(FontNames? names)
    {
        if (names is null) return "regular";
        int fw = names.GetFontWeight();
        if (fw >= 700) return "bold";
        if (fw >= 500) return "medium";
        if (fw > 0 && fw < 400) return "light";
        if (names.IsBold()) return "bold";
        if (ContainsBoldToken(names.GetFontName())) return "bold";
        foreach (var row in SafeFullName(names))
        {
            if (row is not null && row.Length > 3 && ContainsBoldToken(row[3])) return "bold";
        }
        return WeightName(fw, names.GetFontName());
    }

    private static string[][] SafeFullName(FontNames names)
    {
        try
        {
            var r = names.GetFullName();
            return r ?? Array.Empty<string[]>();
        }
        catch
        {
            return Array.Empty<string[]>();
        }
    }

    private static bool ContainsBoldToken(string? s)
    {
        if (s is null) return false;
        var lower = s.ToLowerInvariant();
        return lower.Contains("bold", StringComparison.Ordinal)
               || lower.Contains("black", StringComparison.Ordinal)
               || lower.Contains("heavy", StringComparison.Ordinal);
    }

    private static string WeightName(int fontWeight, string? family)
    {
        if (fontWeight >= 700) return "bold";
        if (fontWeight >= 500) return "medium";
        if (fontWeight > 0 && fontWeight < 400) return "light";
        if (family is not null)
        {
            var lower = family.ToLowerInvariant();
            if (lower.Contains("bold", StringComparison.Ordinal)) return "bold";
            if (lower.Contains("light", StringComparison.Ordinal)
                || lower.Contains("thin", StringComparison.Ordinal)) return "light";
            if (lower.Contains("medium", StringComparison.Ordinal)) return "medium";
        }
        return "regular";
    }

    private static string HexColor(Color? color)
    {
        if (color is null) return "#000000";
        float[] rgb;
        switch (color)
        {
            case DeviceRgb:
                rgb = color.GetColorValue();
                break;
            case DeviceGray:
                {
                    float g = color.GetColorValue()[0];
                    rgb = new[] { g, g, g };
                    break;
                }
            case DeviceCmyk:
                {
                    var v = color.GetColorValue();
                    float c = v[0], m = v[1], y = v[2], k = v[3];
                    rgb = new[]
                    {
                        (1 - c) * (1 - k),
                        (1 - m) * (1 - k),
                        (1 - y) * (1 - k)
                    };
                    break;
                }
            default:
                {
                    // Unknown color space (ICC, Separation, …). Best-effort: use raw values if 3 present.
                    var raw = color.GetColorValue();
                    if (raw is not null && raw.Length >= 3)
                    {
                        rgb = new[] { raw[0], raw[1], raw[2] };
                    }
                    else
                    {
                        return "#000000";
                    }
                    break;
                }
        }
        int r = Clamp(rgb[0]);
        int gc = Clamp(rgb[1]);
        int b = Clamp(rgb[2]);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{gc:X2}{b:X2}");
    }

    private static int Clamp(float v)
    {
        int i = (int)Math.Round(v * 255f, MidpointRounding.AwayFromZero);
        if (i < 0) return 0;
        if (i > 255) return 255;
        return i;
    }

    private static long Key(int page, int mcid) => ((long)page << 32) | (uint)mcid;
}
