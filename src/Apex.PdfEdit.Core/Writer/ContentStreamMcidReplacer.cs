using System.Globalization;
using System.Text;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Layout;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Rewrites a page's content stream so that the text inside targeted marked-content
/// blocks (identified by MCID) is replaced. Unlike whiteout-and-stamp, this touches
/// the payload of the original <c>BDC ... EMC</c> block itself, so the tag tree —
/// which references the block by MCID — automatically reports the new text to
/// accessibility tools and reading order.
///
/// Behaviour: for each target MCID we (a) drop every text-related operator between
/// <c>BDC</c> and <c>EMC</c>, and (b) emit a self-contained replacement text sequence
/// in its place, positioned at the MCID's original bbox origin and drawn with the
/// resolved <see cref="FontStyle"/>. All non-text operators pass through verbatim so
/// the page's graphics, widgets, and marked-content structure survive.
///
/// Not handled (POC scope): nested BDC blocks inside a target MCID, and BDC whose
/// properties come from a named entry in the page's <c>/Properties</c> resource.
/// </summary>
internal sealed class ContentStreamMcidReplacer : PdfCanvasProcessor
{
    private readonly ILogger _log;

    private const float LineHeightMultiplier = 1.2f;

    /// <summary>
    /// How far below the next sibling's top the last wrapped line may descend before we
    /// invoke shrink-to-fit. Set positive (loose) rather than negative (strict): the
    /// customer's UAT explicitly prefers preserved font sizes over strict non-overlap.
    /// </summary>
    private const float SiblingOvershootTolerancePt = 2.0f;

    /// <summary>
    /// Offset from the extractor's bbox-bottom Y up to the text baseline, as a fraction
    /// of the effective font size. Empirically ~0.3 × fontSize on the corpus.
    /// </summary>
    private const float DescenderAdjustRatio = 0.3f;

    private readonly IReadOnlyDictionary<int, SetTextOverlay> _byMcid;
    private readonly WriterFontCache? _fontCache;

    private PdfCanvas _outCanvas = null!;
    private PdfOutputStream _outStream = null!;
    private PageFontInventory? _pageFontInventory;
    private int _currentPageNumber = -1;

    private int _activeTargetMcid = -1;
    private bool _replacementEmittedForActiveBlock;
    private bool _insideTextObject;

    // Snapshots of the most recent Tf/Tm/TL passed-through operator sequences. Re-emitted
    // after our replacement to restore text state.
    private IList<PdfObject>? _lastTfOperands;
    private IList<PdfObject>? _lastTmOperands;
    private double? _lastLeading;

    // Character-spacing (Tc) and word-spacing (Tw) state carried across our injection.
    private IList<PdfObject>? _lastTcOperands;
    private IList<PdfObject>? _lastTwOperands;

    // First Tc/Tw/Tm seen INSIDE the current target BDC.
    private IList<PdfObject>? _firstTcInBlock;
    private IList<PdfObject>? _firstTwInBlock;
    private IList<PdfObject>? _firstTmInBlock;

    // Cumulative text-matrix offset (Td/TD/T*) in text-space since the last Tm.
    private double _dxSinceLastTm;
    private double _dySinceLastTm;

    // Snapshot of dySinceLastTm at the first text-showing op inside the current target BDC.
    private double? _firstTjDyInBlock;

    // Snapshot of lastTc/lastTw at the moment the target BDC opens.
    private IList<PdfObject>? _tcAtBdcOpen;
    private IList<PdfObject>? _twAtBdcOpen;

    // Effective per-character kerning from source's first TJ inside the target BDC (1/1000 em).
    private double? _firstTjAvgKernPer1000;

    private ContentStreamMcidReplacer(
        IReadOnlyDictionary<int, SetTextOverlay> byMcid,
        WriterFontCache? fontCache,
        ILogger? logger)
        : base(new ContentStreamHelpers.NoOpListener())
    {
        _byMcid = byMcid;
        _fontCache = fontCache;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Rewrite <paramref name="page"/>'s content stream, applying the given overlays.</summary>
    internal static void Apply(PdfPage page, IReadOnlyList<SetTextOverlay>? overlays, WriterFontCache? fontCache,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (overlays is null || overlays.Count == 0) return;

        var byMcid = new Dictionary<int, SetTextOverlay>(overlays.Count);
        foreach (var o in overlays) byMcid[o.Mcid] = o;

        var originalBytes = page.GetContentBytes();
        if (originalBytes is null || originalBytes.Length == 0) return;

        var resources = page.GetResources();
        var freshContent = new PdfStream();
        page.GetPdfObject().Put(PdfName.Contents, freshContent);
        page.GetPdfObject().SetModified();
        // Strip Apple-proprietary content hashes — our rewrite invalidates them and PAC 2024
        // catches the mismatch as "Components required" on PDFs that carry them.
        ContentStreamHelpers.StripAppleHashKeys(page);

        var proc = new ContentStreamMcidReplacer(byMcid, fontCache, logger)
        {
            _outCanvas = new PdfCanvas(freshContent, resources, page.GetDocument()),
            _outStream = freshContent.GetOutputStream(),
            _pageFontInventory = PageFontInventory.Of(page),
            _currentPageNumber = page.GetDocument().GetPageNumber(page)
        };
        proc.ProcessContent(originalBytes, resources);
    }

    protected override void InvokeOperator(PdfLiteral op, IList<PdfObject> operands)
    {
        var opName = op.ToString();

        // Flip the text-object flag at the TOP so a suppressed ET (inside the target block)
        // still updates our state — the suppression branch returns early.
        if ("BT".Equals(opName, StringComparison.Ordinal)) _insideTextObject = true;
        else if ("ET".Equals(opName, StringComparison.Ordinal)) _insideTextObject = false;

        // Snapshot text-state ops so we can restore them post-injection.
        if ("Tf".Equals(opName, StringComparison.Ordinal))
        {
            _lastTfOperands = new List<PdfObject>(operands);
        }
        else if ("Tm".Equals(opName, StringComparison.Ordinal))
        {
            _lastTmOperands = new List<PdfObject>(operands);
            if (_activeTargetMcid >= 0 && _firstTmInBlock is null)
            {
                _firstTmInBlock = new List<PdfObject>(operands);
            }
        }
        else if ("Tc".Equals(opName, StringComparison.Ordinal))
        {
            _lastTcOperands = new List<PdfObject>(operands);
            // Capture only the block's OPENING Tc — before the first text-showing op.
            if (_activeTargetMcid >= 0 && _firstTcInBlock is null && _firstTjDyInBlock is null)
            {
                _firstTcInBlock = new List<PdfObject>(operands);
            }
        }
        else if ("Tw".Equals(opName, StringComparison.Ordinal))
        {
            _lastTwOperands = new List<PdfObject>(operands);
            if (_activeTargetMcid >= 0 && _firstTwInBlock is null && _firstTjDyInBlock is null)
            {
                _firstTwInBlock = new List<PdfObject>(operands);
            }
        }
        else if ("TL".Equals(opName, StringComparison.Ordinal) && operands.Count > 0)
        {
            if (operands[0] is PdfNumber n) _lastLeading = n.DoubleValue();
        }
        else if ("TD".Equals(opName, StringComparison.Ordinal) && operands.Count >= 2)
        {
            if (operands[1] is PdfNumber n) _lastLeading = -n.DoubleValue();
        }

        // Track source's cumulative Td/TD/T* GLOBALLY. Tm resets — Td/TD/T* between the Tm
        // and BDC are part of source's real cursor position.
        switch (opName)
        {
            case "Td":
            case "TD":
                if (operands.Count >= 2 && operands[0] is PdfNumber tx && operands[1] is PdfNumber ty)
                {
                    _dxSinceLastTm += tx.DoubleValue();
                    _dySinceLastTm += ty.DoubleValue();
                }
                break;
            case "T*":
                if (_lastLeading is { } lead) _dySinceLastTm -= lead;
                break;
            case "Tm":
                _dxSinceLastTm = 0;
                _dySinceLastTm = 0;
                break;
        }

        if ("BDC".Equals(opName, StringComparison.Ordinal) && operands.Count >= 3)
        {
            int mcid = ExtractInlineMcid(operands[1]);
            if (_activeTargetMcid < 0 && _byMcid.ContainsKey(mcid))
            {
                _activeTargetMcid = mcid;
                _replacementEmittedForActiveBlock = false;
                // Snapshot inherited Tc/Tw at BDC entry.
                _tcAtBdcOpen = _lastTcOperands is null ? null : new List<PdfObject>(_lastTcOperands);
                _twAtBdcOpen = _lastTwOperands is null ? null : new List<PdfObject>(_lastTwOperands);
            }
            WriteOperandsAndOperator(operands);
            return;
        }

        if ("EMC".Equals(opName, StringComparison.Ordinal) && _activeTargetMcid >= 0)
        {
            if (!_replacementEmittedForActiveBlock)
            {
                EmitReplacement(_byMcid[_activeTargetMcid]);
                _replacementEmittedForActiveBlock = true;
            }
            _activeTargetMcid = -1;
            // Clear per-block first-* so subsequent target MCIDs capture their own state fresh.
            _firstTcInBlock = null;
            _firstTwInBlock = null;
            _firstTmInBlock = null;
            _firstTjDyInBlock = null;
            _tcAtBdcOpen = null;
            _twAtBdcOpen = null;
            _firstTjAvgKernPer1000 = null;
            WriteOperandsAndOperator(operands);
            return;
        }

        if (_activeTargetMcid >= 0 && IsTextRelatedOp(opName))
        {
            // Snapshot source's text-line matrix at the first drawn glyph.
            if (_firstTjDyInBlock is null && IsTextShowingOp(opName))
            {
                _firstTjDyInBlock = _dySinceLastTm;
                _firstTjAvgKernPer1000 = AverageTjKerningPer1000(opName, operands);
            }
            // Drop the source's original text op.
            return;
        }

        WriteOperandsAndOperator(operands);
    }

    private void WriteOperandsAndOperator(IList<PdfObject> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (i > 0) _outStream.WriteSpace();
            _outStream.Write(operands[i]);
        }
        _outStream.WriteNewLine();
    }

    private void EmitReplacement(SetTextOverlay overlay)
    {
        var style = overlay.Style;
        var font = ResolveFont(style, overlay);
        var color = StandardFontMapper.ParseHex(style.ColorHex);

        // Font-size priority: style.Size (>=4) → effective Tf × |Tm.d| → approxFromHeight.
        float fontSize;
        if (style.Size >= 4.0f)
        {
            fontSize = style.Size;
        }
        else
        {
            float? srcTfTm = EffectiveFontSizeFromTfTm(_lastTfOperands, _lastTmOperands);
            fontSize = srcTfTm ?? ApproxFromHeight(overlay.Height);
        }

        // Baseline resolution order (see Java for full rationale):
        //  1. First Tm inside the target block → its Y.
        //  2. firstTjDyInBlock transformed through last-Tm scale + translation.
        //  3. overlay.GlyphBaselineY + descender adjustment.
        //  4. Last-Tm Y fallback.
        //  5. overlay.Y final fallback.
        float baselineY;
        float? blockTmY = TmYFromOperands(_firstTmInBlock);
        if (blockTmY is { } btm)
        {
            baselineY = btm;
        }
        else if (_firstTjDyInBlock is { } dy)
        {
            float? tmScaleD = TmDFromOperands(_lastTmOperands);
            float? tmTransF = TmYFromOperands(_lastTmOperands);
            double scale = tmScaleD ?? 1.0;
            double trans = tmTransF ?? 0.0;
            baselineY = (float)(trans + scale * dy);
        }
        else if (double.IsFinite(overlay.GlyphBaselineY))
        {
            baselineY = (float)overlay.GlyphBaselineY + DescenderAdjustRatio * fontSize;
        }
        else
        {
            baselineY = LastTmY((float)overlay.Y);
        }

        float bboxWidth = (float)overlay.Width;
        float bboxHeight = (float)overlay.Height;
        var lines = WrapText(overlay.NewContent, font, fontSize, bboxWidth);
        float lineHeight = fontSize * EffectiveLeadingMultiplier(style);

        // Source-single-line preservation.
        bool sourceIsSingleLine = bboxHeight < lineHeight * 1.5f;
        if (sourceIsSingleLine && lines.Count > 1)
        {
            float requiredWidth = font.GetWidth(overlay.NewContent, fontSize);
            _log.LogInformation(
                "MCID {Mcid} on page {Page}: source paragraph fits one line (bbox h={H} <= 1.5 × lineHeight={LH}); collapsing wrapped edit to preserve Adobe Format-panel typography (width {W} > bbox {BW}); may extend horizontally past bbox",
                overlay.Mcid, _currentPageNumber,
                bboxHeight.ToString("F2", CultureInfo.InvariantCulture),
                lineHeight.ToString("F2", CultureInfo.InvariantCulture),
                requiredWidth.ToString("F2", CultureInfo.InvariantCulture),
                bboxWidth.ToString("F2", CultureInfo.InvariantCulture));
            lines = new List<string> { overlay.NewContent };
        }

        // Multi-line overflow: collapse to single line if vertical doesn't fit above next sibling.
        if (lines.Count > 1)
        {
            float availableDescent;
            if (double.IsFinite(overlay.NextSiblingTopY))
            {
                availableDescent = (float)(baselineY - overlay.NextSiblingTopY) + SiblingOvershootTolerancePt;
            }
            else
            {
                availableDescent = bboxHeight - lineHeight;
            }
            int maxLinesThatFit = 1 + (int)Math.Floor(availableDescent / lineHeight);
            if (lines.Count > maxLinesThatFit)
            {
                float requiredWidth = font.GetWidth(overlay.NewContent, fontSize);
                _log.LogInformation(
                    "MCID {Mcid} on page {Page}: {Have} wrapped lines but only {Fit} fit above sibling — collapsing to one line at source {Size} pt (width {W} > bbox {BW}); may extend horizontally past bbox",
                    overlay.Mcid, _currentPageNumber, lines.Count, maxLinesThatFit,
                    fontSize.ToString("F2", CultureInfo.InvariantCulture),
                    requiredWidth.ToString("F2", CultureInfo.InvariantCulture),
                    bboxWidth.ToString("F2", CultureInfo.InvariantCulture));
                lines = new List<string> { overlay.NewContent };
            }
        }

        // Two-branch emit — see Java Javadoc for the state-machine rationale.
        if (!_insideTextObject) _outCanvas.BeginText();

        // Character-spacing / word-spacing: prefer opening intent, fall back to inherited state.
        IList<PdfObject>? emitTcOperands = _firstTcInBlock ?? _tcAtBdcOpen;
        IList<PdfObject>? emitTwOperands = _firstTwInBlock ?? _twAtBdcOpen;

        // Fold source's per-glyph TJ kerning into an equivalent Tc.
        if (_firstTjAvgKernPer1000 is { } avgKern && Math.Abs(avgKern) > 0.01 && emitTcOperands is not null)
        {
            double baseTc = NumFromOperands(emitTcOperands);
            double effectiveTc = baseTc + avgKern * fontSize / 1000.0;
            double rounded = Math.Round(effectiveTc * 10000.0) / 10000.0;
            emitTcOperands = new List<PdfObject>
            {
                new PdfNumber(rounded),
                new PdfLiteral("Tc")
            };
        }
        if (emitTcOperands is not null) WriteOperandsAndOperator(emitTcOperands);
        if (emitTwOperands is not null) WriteOperandsAndOperator(emitTwOperands);

        // Multi-run vs single-style emit.
        List<InlineSegment>? segments = lines.Count == 1
            ? SegmentByRuns(lines[0], overlay.SourceRuns, style)
            : null;

        if (segments is not null && segments.Count > 1)
        {
            var line = lines[0];
            float lineX = AlignedX(overlay, font, fontSize, line);
            _outCanvas.SetTextMatrix(lineX, baselineY);
            foreach (var seg in segments)
            {
                if (seg.Text.Length == 0) continue;
                var segFont = ResolveRunFont(seg.Style, font);
                var segColor = StandardFontMapper.ParseHex(seg.Style.ColorHex);
                float segSize = seg.Style.Size >= 4.0f ? seg.Style.Size : fontSize;
                _outCanvas.SetFillColor(segColor).SetFontAndSize(segFont, segSize).ShowText(seg.Text);
            }
        }
        else
        {
            _outCanvas.SetFillColor(color).SetFontAndSize(font, fontSize).SetLeading(lineHeight);
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                float lineX = AlignedX(overlay, font, fontSize, line);
                float lineY = baselineY - i * lineHeight;
                _outCanvas.SetTextMatrix(lineX, lineY).ShowText(line);
            }
        }

        // Restore state.
        if (_lastTfOperands is not null) WriteOperandsAndOperator(_lastTfOperands);
        if (_lastTcOperands is not null) WriteOperandsAndOperator(_lastTcOperands);
        if (_lastTwOperands is not null) WriteOperandsAndOperator(_lastTwOperands);
        if (_lastTmOperands is not null)
        {
            WriteOperandsAndOperator(_lastTmOperands);
        }
        else
        {
            _outStream.WriteString("1 0 0 1 0 0 Tm\n");
        }
        if (_lastLeading is { } tl)
        {
            _outStream.WriteString(tl.ToString("F6", CultureInfo.InvariantCulture) + " TL\n");
        }
        if (_dxSinceLastTm != 0.0 || _dySinceLastTm != 0.0)
        {
            _outStream.WriteString(string.Format(
                CultureInfo.InvariantCulture, "{0:F6} {1:F6} Td\n", _dxSinceLastTm, _dySinceLastTm));
        }
        if (!_insideTextObject) _outCanvas.EndText();
    }

    /// <summary>
    /// Greedy word-wrap: pack words into a line while the running measured width fits within
    /// <paramref name="maxWidth"/>. Words wider than the bbox go on their own line (overflow
    /// to the right rather than break mid-word). Empty input yields <c>[""]</c>.
    /// </summary>
    internal static List<string> WrapText(string? text, PdfFont font, float fontSize, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }
        if (maxWidth <= 0.0f)
        {
            lines.Add(text);
            return lines;
        }
        // Split on embedded newlines first so \n becomes an explicit hard break.
        foreach (var segment in text.Split('\n'))
        {
            WrapSegment(segment, font, fontSize, maxWidth, lines);
        }
        if (lines.Count == 0) lines.Add(string.Empty);
        return lines;
    }

    private static void WrapSegment(string segment, PdfFont font, float fontSize, float maxWidth, List<string> output)
    {
        if (segment.Length == 0)
        {
            output.Add(string.Empty);
            return;
        }
        var words = segment.Split(' ');
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }
            var candidate = current.ToString() + " " + word;
            if (font.GetWidth(candidate, fontSize) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
            }
            else
            {
                output.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }
        if (current.Length > 0) output.Add(current.ToString());
    }

    private float LastTmY(float fallback) => TmYFromOperands(_lastTmOperands) ?? fallback;

    /// <summary>
    /// Extract the Y translation (<c>f</c> operand at index 5) from a Tm operand list of the
    /// form <c>[a, b, c, d, e, f, PdfLiteral("Tm")]</c>. Returns null on missing / wrong-shape.
    /// </summary>
    internal static float? TmYFromOperands(IList<PdfObject>? ops)
    {
        if (ops is null || ops.Count < 7) return null;
        return ops[5] is PdfNumber num ? num.FloatValue() : null;
    }

    /// <summary>
    /// Sibling of <see cref="TmYFromOperands"/> — returns the vertical scale component (<c>d</c>).
    /// Used to convert text-space Δy back to user-space (source often sets <c>Tf 1</c> +
    /// scaled <c>Tm</c>).
    /// </summary>
    internal static float? TmDFromOperands(IList<PdfObject>? ops)
    {
        if (ops is null || ops.Count < 7) return null;
        return ops[3] is PdfNumber num ? num.FloatValue() : null;
    }

    /// <summary>
    /// Font resolution chain, in order of preference:
    /// <list type="number">
    ///   <item><b>Exact</b> — source PDF's own subsetted font for this MCID's <see cref="FontStyle.SourceFontObjNumber"/>.</item>
    ///   <item><b>Family+weight embedded</b> — walk candidates, pick the first that can render every glyph.</item>
    ///   <item><b>System</b> — same family + weight loaded fresh from OS.</item>
    ///   <item><b>Universal fallback</b> — guaranteed-embedded Arial/Times/Courier.</item>
    ///   <item><b>Standard-14</b> — non-embedded last resort.</item>
    /// </list>
    /// </summary>
    private PdfFont ResolveFont(FontStyle style, SetTextOverlay overlay)
    {
        if (_pageFontInventory is not null && style is not null)
        {
            if (style.SourceFontObjNumber is { } objNum)
            {
                var exact = _pageFontInventory.FindByObjNumber(objNum);
                if (exact is not null) return exact;
            }
            var candidates = _pageFontInventory.CandidatesByFamilyAndWeight(style.Family, style.Weight);
            foreach (var candidate in candidates)
            {
                if (PageFontInventory.CanRender(candidate, overlay.NewContent))
                {
                    return candidate;
                }
            }
            if (candidates.Count > 0)
            {
                var candidate = candidates[0];
                int missing = PageFontInventory.FirstMissingCodePoint(candidate, overlay.NewContent);
                _log.LogWarning(
                    "MCID {Mcid} on page {Page}: no embedded '{Family}' candidate can render the new text — using {Fallback} anyway (missing U+{Hex} '{Char}')",
                    overlay.Mcid, _currentPageNumber, style.Family,
                    FamilyLabel(candidate),
                    missing.ToString("X4", CultureInfo.InvariantCulture),
                    missing >= 0 ? char.ConvertFromUtf32(missing) : "?");
                return candidate;
            }
        }

        if (style?.Family is { Length: > 0 } fam && !string.IsNullOrWhiteSpace(fam))
        {
            var system = _fontCache is not null
                ? _fontCache.Load(style.Family, style.Weight)
                : SystemFontLocator.Load(style.Family, style.Weight);
            if (system is not null && PageFontInventory.CanRender(system, overlay.NewContent))
            {
                _log.LogInformation(
                    "MCID {Mcid} on page {Page}: no embedded '{Family}' font found, using system {Sys}",
                    overlay.Mcid, _currentPageNumber, style.Family, FamilyLabel(system));
                return system;
            }
        }

        var universal = _fontCache is not null
            ? _fontCache.LoadUniversalFallback(style)
            : SystemFontLocator.LoadUniversalFallback(style);
        if (universal is not null && PageFontInventory.CanRender(universal, overlay.NewContent))
        {
            _log.LogInformation(
                "MCID {Mcid} on page {Page}: no matching embedded font, using universal fallback {Sys}",
                overlay.Mcid, _currentPageNumber, FamilyLabel(universal));
            return universal;
        }

        _log.LogWarning(
            "MCID {Mcid} on page {Page}: falling back to non-embedded standard-14 — will fail PDF/UA Font-embedding check",
            overlay.Mcid, _currentPageNumber);
        return PdfFontFactory.CreateFont(StandardFontMapper.MapToStandardFont(style!));
    }

    private static string FamilyLabel(PdfFont font)
    {
        try
        {
            return font.GetFontProgram().GetFontNames().GetFontName();
        }
        catch
        {
            return "(unknown)";
        }
    }

    /// <summary>
    /// Compute the text-matrix X so a single line of the new content sits inside the original
    /// bbox consistently with the node's classified alignment. JUSTIFIED and UNKNOWN fall
    /// through to LEFT for POC.
    /// </summary>
    private static float AlignedX(SetTextOverlay overlay, PdfFont font, float fontSize, string lineText)
    {
        var a = overlay.Alignment;
        if (a == Alignment.Left || a == Alignment.Justified || a == Alignment.Unknown)
        {
            return (float)overlay.X;
        }
        float textWidth = font.GetWidth(lineText, fontSize);
        float bboxWidth = (float)overlay.Width;
        if (a == Alignment.Center)
        {
            return (float)overlay.X + (bboxWidth - textWidth) / 2f;
        }
        // Right
        return (float)overlay.X + bboxWidth - textWidth;
    }

    private static float ApproxFromHeight(double h)
    {
        double approx = h > 0 ? h * 0.85 : 10.0;
        if (approx < 5.0) approx = 5.0;
        if (approx > 72.0) approx = 72.0;
        return (float)approx;
    }

    /// <summary>
    /// One piece of the new-content string with the style it should render at.
    /// <see cref="SegmentByRuns"/> decomposes a single wrapped line into a list of these.
    /// </summary>
    internal sealed record InlineSegment(string Text, FontStyle Style);

    /// <summary>
    /// Decompose <paramref name="line"/> into inline segments, tagging any substring that
    /// appears verbatim in a source <see cref="TextRun"/> with that run's style. Unmatched
    /// gaps get the <paramref name="primary"/> style. Runs are matched in source order.
    /// </summary>
    internal static List<InlineSegment> SegmentByRuns(string? line, IReadOnlyList<TextRun>? sourceRuns, FontStyle primary)
    {
        if (sourceRuns is null || sourceRuns.Count < 2 || string.IsNullOrEmpty(line))
        {
            return new List<InlineSegment> { new(line ?? string.Empty, primary) };
        }
        var matches = new List<(int Start, int End, int RunIndex)>();
        int searchFrom = 0;
        for (int i = 0; i < sourceRuns.Count; i++)
        {
            var run = sourceRuns[i];
            var needle = run.Text;
            if (string.IsNullOrEmpty(needle)) continue;
            int at = line.IndexOf(needle, searchFrom, StringComparison.Ordinal);
            if (at < 0) continue;
            matches.Add((at, at + needle.Length, i));
            searchFrom = at + needle.Length;
        }
        if (matches.Count == 0)
        {
            return new List<InlineSegment> { new(line, primary) };
        }
        var output = new List<InlineSegment>();
        int cursor = 0;
        foreach (var m in matches)
        {
            if (m.Start > cursor) output.Add(new InlineSegment(line[cursor..m.Start], primary));
            var runStyle = sourceRuns[m.RunIndex].Style;
            if (runStyle.Size < 4.0f && primary.Size >= 4.0f)
            {
                runStyle = new FontStyle(runStyle.Family, primary.Size,
                    runStyle.Weight, runStyle.ColorHex,
                    runStyle.SourceFontObjNumber, runStyle.LeadingRatio);
            }
            output.Add(new InlineSegment(line[m.Start..m.End], runStyle));
            cursor = m.End;
        }
        if (cursor < line.Length) output.Add(new InlineSegment(line[cursor..], primary));
        return output;
    }

    /// <summary>
    /// Resolve the PdfFont for one inline segment's style. Same three-tier chain as
    /// <see cref="ResolveFont"/> but keyed on the segment's own style.
    /// </summary>
    private PdfFont ResolveRunFont(FontStyle segStyle, PdfFont primaryFont)
    {
        if (_pageFontInventory is not null && segStyle.SourceFontObjNumber is { } objNum)
        {
            var exact = _pageFontInventory.FindByObjNumber(objNum);
            if (exact is not null) return exact;
        }
        if (_pageFontInventory is not null && segStyle.Family is not null)
        {
            foreach (var c in _pageFontInventory.CandidatesByFamilyAndWeight(segStyle.Family, segStyle.Weight))
            {
                return c;
            }
        }
        return primaryFont;
    }

    /// <summary>
    /// Multiplier to convert emitted font size into inter-line spacing. Prefers source's
    /// TL/Tf ratio; falls back to 1.2 when source never set TL.
    /// </summary>
    internal static float EffectiveLeadingMultiplier(FontStyle? style)
    {
        if (style is not null && style.LeadingRatio > 0f) return style.LeadingRatio;
        return LineHeightMultiplier;
    }

    /// <summary>
    /// Effective source font size derived from Tf × |Tm.d|. Common patterns:
    /// <list type="bullet">
    ///   <item><c>10 Tf</c> + identity Tm → 10 × 1 = 10pt.</item>
    ///   <item><c>1 Tf</c> + <c>12 0 0 12 x y Tm</c> → 1 × 12 = 12pt.</item>
    /// </list>
    /// Returns null when Tf hasn't been seen or the derived size is outside a sane [4, 200] range.
    /// </summary>
    internal static float? EffectiveFontSizeFromTfTm(IList<PdfObject>? tfOperands, IList<PdfObject>? tmOperands)
    {
        if (tfOperands is null || tfOperands.Count < 2) return null;
        if (tfOperands[1] is not PdfNumber tfSize) return null;
        double size = tfSize.DoubleValue();
        if (!(size > 0)) return null;
        double scale = 1.0;
        if (tmOperands is not null && tmOperands.Count >= 4 && tmOperands[3] is PdfNumber d)
        {
            double dv = Math.Abs(d.DoubleValue());
            if (dv > 0.01) scale = dv;
        }
        double effective = size * scale;
        if (effective < 4.0 || effective > 200.0) return null;
        return (float)effective;
    }

    private static int ExtractInlineMcid(PdfObject props)
    {
        if (props is PdfDictionary d)
        {
            var v = d.Get(PdfName.MCID);
            if (v is PdfNumber n) return n.IntValue();
        }
        return -1;
    }

    private static bool IsTextShowingOp(string name)
        => name == "Tj" || name == "TJ" || name == "'" || name == "\"";

    /// <summary>First numeric operand as a double, or 0.0 on null/empty/non-number.</summary>
    private static double NumFromOperands(IList<PdfObject>? operands)
    {
        if (operands is null || operands.Count == 0) return 0.0;
        return operands[0] is PdfNumber n ? n.DoubleValue() : 0.0;
    }

    /// <summary>
    /// For a <c>TJ</c> op whose operand is an array of strings + per-glyph kerning numbers,
    /// return the average kerning per character in text-space (1/1000 em units). Null for
    /// plain Tj/'/", empty arrays, or zero character count.
    /// </summary>
    private static double? AverageTjKerningPer1000(string opName, IList<PdfObject> operands)
    {
        if (opName != "TJ" || operands.Count == 0) return null;
        if (operands[0] is not PdfArray arr) return null;
        double kernSum = 0;
        int charCount = 0;
        for (int i = 0; i < arr.Size(); i++)
        {
            var el = arr.Get(i);
            if (el is PdfNumber n)
            {
                kernSum += n.DoubleValue();
            }
            else if (el is PdfString s)
            {
                // Rough glyph count via byte length — over-counts CID (2b/glyph) by 2×,
                // still close enough for Adobe's 2-decimal rounding.
                charCount += s.GetValueBytes().Length;
            }
        }
        if (charCount == 0) return null;
        return kernSum / charCount;
    }

    /// <summary>
    /// All operators from PDF §9 (Text) — showing + positioning + state. Inside a target MCID
    /// our replacement emits its own text sequence, so every source-side text op must be dropped.
    /// BT/ET are included so we can emit a controlled BT/ET pair at EMC time.
    /// </summary>
    private static bool IsTextRelatedOp(string name) => name switch
    {
        "Tj" or "TJ" or "'" or "\"" => true,
        "Td" or "TD" or "Tm" or "T*" => true,
        "Tc" or "Tw" or "Tz" or "TL" or "Tf" or "Tr" or "Ts" => true,
        "BT" or "ET" => true,
        _ => false
    };
}
