using System.Runtime.CompilerServices;
using iText.IO.Font;
using iText.IO.Font.Otf;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Snapshots the fonts embedded in a page's <c>/Font</c> resource dict so
/// <c>ContentStreamMcidReplacer</c> can prefer the source PDF's own typeface
/// when it emits an edit — instead of always mapping down to a standard-14
/// substitute via <see cref="StandardFontMapper"/>.
///
/// Two lookups matter:
/// <list type="bullet">
///   <item><see cref="FindByFamilyAndWeight"/> — find the <see cref="PdfFont"/> whose
///       base name matches the family the <c>SourcePdfFontResolver</c> recorded for
///       the target MCID.</item>
///   <item><see cref="CanRender"/> — check up-front whether the new text's glyphs are
///       all in the embedded font's subset. Rev 1 §3 decided that edits introducing
///       missing glyphs fail rather than silently substitute.</item>
/// </list>
///
/// Font names in the PDF resource dict typically look like <c>/VHIJCY+Verdana</c>
/// (six-letter subset prefix + family) or <c>/PYGUWK+Verdana,Bold</c> (family + weight
/// suffix). The <see cref="FamilyStem"/> helper strips both so the family match is
/// robust across subsets.
/// </summary>
public sealed class PageFontInventory
{
    private readonly IReadOnlyDictionary<PdfName, PdfFont> _fonts;

    /// <summary>
    /// Characters ACTUALLY rendered by each font on this document, keyed by the font
    /// dict's indirect reference. Populated by scanning the document's content streams.
    /// An empty map (or missing entry for a given font) means we have no rendered-chars
    /// data for that font — <see cref="CanRenderStrict"/> falls back to the cmap check
    /// in that case.
    /// </summary>
    private readonly IReadOnlyDictionary<PdfIndirectReference, HashSet<int>> _renderedCharsByFontRef;

    private PageFontInventory(
        IReadOnlyDictionary<PdfName, PdfFont> fonts,
        IReadOnlyDictionary<PdfIndirectReference, HashSet<int>> renderedChars)
    {
        _fonts = fonts;
        _renderedCharsByFontRef = renderedChars;
    }

    /// <summary>
    /// Build an inventory of every <see cref="PdfFont"/> referenced from <paramref name="page"/>'s
    /// resource dict, plus a per-font set of characters the document actually rendered
    /// (via a one-shot content-stream scan). Broken entries and scan failures are
    /// silently skipped.
    /// </summary>
    public static PageFontInventory Of(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var map = new Dictionary<PdfName, PdfFont>();
        var fontDict = page.GetResources().GetResource(PdfName.Font);
        if (fontDict is not null)
        {
            foreach (var name in fontDict.KeySet())
            {
                var value = fontDict.Get(name);
                if (value is not PdfDictionary dict) continue;
                try
                {
                    map[name] = PdfFontFactory.CreateFont(dict);
                }
                catch
                {
                    // Skip entries iText can't materialise (rare — usually broken producers).
                }
            }
        }
        // Cross-page rendered-chars scan: a subset font on this page is shared across
        // the whole document, so a char rendered by that font on any other page has
        // its outline in the shared embedded subset. Cached per-document so the walk
        // runs once.
        var rendered = CrossPageRenderedChars(page.GetDocument());
        return new PageFontInventory(map, rendered);
    }

    private static readonly ConditionalWeakTable<PdfDocument, IReadOnlyDictionary<PdfIndirectReference, HashSet<int>>>
        DocRenderedCache = new();

    private static IReadOnlyDictionary<PdfIndirectReference, HashSet<int>> CrossPageRenderedChars(PdfDocument? doc)
    {
        if (doc is null) return new Dictionary<PdfIndirectReference, HashSet<int>>();
        if (DocRenderedCache.TryGetValue(doc, out var cached)) return cached;

        var merged = new Dictionary<PdfIndirectReference, HashSet<int>>();
        int pageCount = doc.GetNumberOfPages();
        for (int p = 1; p <= pageCount; p++)
        {
            try
            {
                var pageChars = ScanRenderedChars(doc.GetPage(p));
                foreach (var kv in pageChars)
                {
                    if (!merged.TryGetValue(kv.Key, out var set))
                    {
                        set = new HashSet<int>();
                        merged[kv.Key] = set;
                    }
                    set.UnionWith(kv.Value);
                }
            }
            catch
            {
                // Skip pages we can't scan.
            }
        }
        DocRenderedCache.Add(doc, merged);
        return merged;
    }

    /// <summary>
    /// One-shot content-stream scan to record which characters each font on the
    /// page actually rendered. Needed because subset fonts routinely declare more
    /// characters in their encoding cmap than the embedded outline program actually
    /// carries — cross-checking against this set catches the subset gap before we
    /// emit .notdef.
    /// </summary>
    private static Dictionary<PdfIndirectReference, HashSet<int>> ScanRenderedChars(PdfPage page)
    {
        var output = new Dictionary<PdfIndirectReference, HashSet<int>>();
        try
        {
            var listener = new RenderedCharsListener(output);
            var proc = new PdfCanvasProcessor(listener);
            proc.ProcessPageContent(page);
        }
        catch
        {
            // Scan failure — return whatever we gathered before the exception.
        }
        return output;
    }

    private sealed class RenderedCharsListener : IEventListener
    {
        private static readonly ICollection<EventType> SupportedEvents =
            new HashSet<EventType> { EventType.RENDER_TEXT };

        private readonly Dictionary<PdfIndirectReference, HashSet<int>> _out;

        public RenderedCharsListener(Dictionary<PdfIndirectReference, HashSet<int>> output)
        {
            _out = output;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            if (data is not TextRenderInfo tri) return;
            var f = tri.GetFont();
            if (f is null) return;
            var fObj = f.GetPdfObject();
            if (fObj is null) return;
            var refr = fObj.GetIndirectReference();
            if (refr is null) return;
            var text = tri.GetText();
            if (string.IsNullOrEmpty(text)) return;
            if (!_out.TryGetValue(refr, out var chars))
            {
                chars = new HashSet<int>();
                _out[refr] = chars;
            }
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                chars.Add(cp);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
            }
        }

        public ICollection<EventType> GetSupportedEvents() => SupportedEvents;
    }

    /// <summary>Immutable view of the resource-name → <see cref="PdfFont"/> map.</summary>
    public IReadOnlyDictionary<PdfName, PdfFont> Fonts => _fonts;

    /// <summary>
    /// Look up a page font by the object number of its resource dict. Used to pick
    /// the exact source-side font a given MCID was originally rendered with — see
    /// <see cref="FontStyle.SourceFontObjNumber"/>. Object numbers are file-persistent
    /// (part of the PDF xref) so they match across the two <see cref="PdfDocument"/>
    /// instances of the same source file (resolver's read-only + writer's stamp-mode).
    /// </summary>
    public PdfFont? FindByObjNumber(int objNumber)
    {
        foreach (var f in _fonts.Values)
        {
            var obj = f.GetPdfObject();
            if (obj is null) continue;
            var refr = obj.GetIndirectReference();
            if (refr is null) continue;
            if (refr.GetObjNumber() == objNumber) return f;
        }
        return null;
    }

    /// <summary>
    /// Best-effort match on family (subset-prefix stripped, case-insensitive) plus weight.
    /// Returns the first font whose weight matches; otherwise the first same-family font.
    /// Null if nothing matches on family.
    /// </summary>
    public PdfFont? FindByFamilyAndWeight(string? family, string? weight)
    {
        var ordered = CandidatesByFamilyAndWeight(family, weight);
        return ordered.Count == 0 ? null : ordered[0];
    }

    /// <summary>
    /// All fonts on the page whose family stem matches, ordered by preference:
    /// weight-matching simple → weight-matching Type0 → weight-mismatched simple →
    /// weight-mismatched Type0. Pages often embed the same PostScript BaseFont
    /// twice (once as Type0 + once as Type1/TrueType); different MCIDs render
    /// through different twins, each holding a disjoint glyph subset. The writer
    /// walks this list and picks the first entry where <see cref="CanRender"/>
    /// is true for the new content.
    /// </summary>
    public IReadOnlyList<PdfFont> CandidatesByFamilyAndWeight(string? family, string? weight)
    {
        if (string.IsNullOrWhiteSpace(family)) return Array.Empty<PdfFont>();
        var target = FamilyStem(family);
        bool wantBold = string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase);

        var weightSimple = new List<PdfFont>();
        var weightType0 = new List<PdfFont>();
        var otherSimple = new List<PdfFont>();
        var otherType0 = new List<PdfFont>();
        foreach (var f in _fonts.Values)
        {
            var baseName = BaseFontName(f);
            if (!string.Equals(target, FamilyStem(baseName), StringComparison.Ordinal)) continue;
            bool simple = !IsType0(f);
            bool weightOk = wantBold == IsBold(f, baseName);
            if (weightOk && simple) weightSimple.Add(f);
            else if (weightOk) weightType0.Add(f);
            else if (simple) otherSimple.Add(f);
            else otherType0.Add(f);
        }
        var output = new List<PdfFont>(weightSimple.Count + weightType0.Count + otherSimple.Count + otherType0.Count);
        output.AddRange(weightSimple);
        output.AddRange(weightType0);
        output.AddRange(otherSimple);
        output.AddRange(otherType0);
        return output;
    }

    /// <summary>
    /// All fonts across the whole document whose family stem matches, deduplicated by
    /// PDF object number and ordered like <see cref="CandidatesByFamilyAndWeight"/>.
    /// Enables cross-page glyph pooling: page N's subset may lack an outline that page M's
    /// subset carries — for chars a single page's font can't render, the writer can walk
    /// this doc-wide pool before falling back to the universal fallback.
    /// </summary>
    public static IReadOnlyList<PdfFont> DocCandidatesByFamilyAndWeight(
        PdfDocument? doc, string? family, string? weight)
    {
        if (doc is null || string.IsNullOrWhiteSpace(family)) return Array.Empty<PdfFont>();
        var target = FamilyStem(family);
        bool wantBold = string.Equals(weight, "bold", StringComparison.OrdinalIgnoreCase);

        var byObj = new Dictionary<int, PdfFont>();
        int pageCount = doc.GetNumberOfPages();
        for (int p = 1; p <= pageCount; p++)
        {
            try
            {
                var pi = Of(doc.GetPage(p));
                foreach (var f in pi._fonts.Values)
                {
                    if (!string.Equals(target, FamilyStem(BaseFontName(f)), StringComparison.Ordinal)) continue;
                    var refr = f.GetPdfObject()?.GetIndirectReference();
                    if (refr is null) continue;
                    byObj.TryAdd(refr.GetObjNumber(), f);
                }
            }
            catch
            {
                // Skip pages whose inventory can't be built.
            }
        }
        var weightSimple = new List<PdfFont>();
        var weightType0 = new List<PdfFont>();
        var otherSimple = new List<PdfFont>();
        var otherType0 = new List<PdfFont>();
        foreach (var f in byObj.Values)
        {
            var baseName = BaseFontName(f);
            bool simple = !IsType0(f);
            bool weightOk = wantBold == IsBold(f, baseName);
            if (weightOk && simple) weightSimple.Add(f);
            else if (weightOk) weightType0.Add(f);
            else if (simple) otherSimple.Add(f);
            else otherType0.Add(f);
        }
        var output = new List<PdfFont>(weightSimple.Count + weightType0.Count + otherSimple.Count + otherType0.Count);
        output.AddRange(weightSimple);
        output.AddRange(weightType0);
        output.AddRange(otherSimple);
        output.AddRange(otherType0);
        return output;
    }

    /// <summary>True if <paramref name="f"/> is a Type0/CID composite font.</summary>
    private static bool IsType0(PdfFont f)
    {
        var dict = f.GetPdfObject();
        if (dict is null) return false;
        var subtype = dict.Get(PdfName.Subtype);
        return PdfName.Type0.Equals(subtype);
    }

    /// <summary>
    /// True if the font can encode every code point in <paramref name="text"/>. Whitespace
    /// control chars are treated as always renderable.
    ///
    /// For each code point we go beyond <see cref="PdfFont.ContainsGlyph"/> and also inspect
    /// the underlying <see cref="Glyph"/>. For CID-keyed Type0 fonts embedded as subsets,
    /// <c>ContainsGlyph</c> reports coverage per the /ToUnicode CMap, but the actual glyph
    /// outline program in the subset only holds shapes for the characters originally used.
    /// Verifying <c>Glyph.GetCode() &gt; 0</c> catches the subset-gap case.
    /// </summary>
    public static bool CanRender(PdfFont? font, string? text)
    {
        if (font is null) return false;
        if (string.IsNullOrEmpty(text)) return true;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (!IsSkippableWhitespace(cp))
            {
                if (!font.ContainsGlyph(cp)) return false;
                var g = font.GetGlyph(cp);
                if (g is null || g.GetCode() <= 0) return false;
            }
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
        return true;
    }

    /// <summary>
    /// Instance-level canRender that additionally requires each character to have been
    /// ACTUALLY rendered by <paramref name="font"/> on this document. Combines the cmap
    /// check of <see cref="CanRender"/> with a lookup against the rendered-chars set
    /// captured during construction. Catches the "encoding cmap covers the char but the
    /// embedded subset lacks the outline" failure mode.
    ///
    /// Returns true when we have no rendered-chars data for the font (e.g. it was added
    /// to the page's resources but never used) — in that case we fall back to the cmap
    /// check alone; being lenient here is safer than false-negativing every candidate.
    /// </summary>
    public bool CanRenderStrict(PdfFont? font, string? text)
    {
        if (!CanRender(font, text)) return false;
        if (font is null || string.IsNullOrEmpty(text)) return true;
        var obj = font.GetPdfObject();
        if (obj is null) return true;
        var refr = obj.GetIndirectReference();
        if (refr is null) return true;
        if (!_renderedCharsByFontRef.TryGetValue(refr, out var rendered) || rendered is null || rendered.Count == 0) return true;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (!IsSkippableWhitespace(cp) && !rendered.Contains(cp)) return false;
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
        return true;
    }

    private static bool IsSkippableWhitespace(int cp) => cp == '\n' || cp == '\r' || cp == '\t';

    /// <summary>First code point that <paramref name="font"/> can't render, or -1 if all are covered.</summary>
    public static int FirstMissingCodePoint(PdfFont? font, string? text)
    {
        if (font is null || text is null) return -1;
        for (int i = 0; i < text.Length;)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (!font.ContainsGlyph(cp)) return cp;
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }
        return -1;
    }

    private static string BaseFontName(PdfFont font)
    {
        var fp = font.GetFontProgram();
        if (fp is null) return string.Empty;
        var names = fp.GetFontNames();
        return names?.GetFontName() ?? string.Empty;
    }

    /// <summary>
    /// Strip a six-letter subset prefix (<c>"ABCDEF+..."</c>) and any <c>",Bold"</c>-style
    /// or <c>"-Bold"</c>-style weight suffix. Lower-case for case-insensitive comparison.
    /// </summary>
    internal static string FamilyStem(string? s)
    {
        if (s is null) return string.Empty;
        int plus = s.IndexOf('+');
        if (plus >= 0 && plus <= 6) s = s[(plus + 1)..];
        int comma = s.IndexOf(',');
        if (comma >= 0) s = s[..comma];
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return s.ToLowerInvariant();
    }

    private static bool IsBold(PdfFont font, string? baseName)
    {
        var fp = font.GetFontProgram();
        if (fp?.GetFontNames() is not null && fp.GetFontNames().GetFontWeight() >= 700) return true;
        if (baseName is null) return false;
        var lower = baseName.ToLowerInvariant();
        return lower.Contains("bold", StringComparison.Ordinal)
               || lower.Contains("black", StringComparison.Ordinal)
               || lower.Contains("heavy", StringComparison.Ordinal);
    }
}
