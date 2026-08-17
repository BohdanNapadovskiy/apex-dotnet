using System.Globalization;
using System.Text;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Layout;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Draws a brand-new tagged paragraph on a copied page (Slice 2 of the addParagraph flow).
/// Per overlay:
/// <list type="number">
///   <item>Locate the insertion parent by walking the copied <see cref="PdfStructTreeRoot"/>
///       for the donor sibling's (page, mcid). The donor's containing <see cref="PdfStructElem"/>
///       is the sibling; its parent is where the new tag goes.</item>
///   <item>Allocate a fresh MCID via <see cref="PdfPage.GetNextMcid"/>.</item>
///   <item>Emit a self-contained <c>q ... /Role &lt;&lt;/MCID N&gt;&gt; BDC BT ... ET EMC ... Q</c>
///       sequence at the overlay's computed baseline. Font resolution goes through the same
///       three-tier chain as <see cref="ContentStreamMcidReplacer"/>.</item>
///   <item>Attach a new <see cref="PdfStructElem"/> of the requested role as a kid of the
///       insertion parent, with a <see cref="PdfMcrNumber"/> pointing at the fresh MCID.</item>
/// </list>
///
/// Push-down reflow of downstream siblings is intentionally NOT done — the engine's
/// placement calc trusts the caller to pick an insertion point that has room.
/// </summary>
internal static class AddParagraphStamper
{
    /// <summary>
    /// Adobe's Format-panel "line spacing" readout applies a ~0.98 discount to the
    /// emitted TL. Bumping emitted TL by 1/0.98 makes Adobe report the intended ratio.
    /// Baseline math uses the same compensated value so on-page geometry stays consistent.
    /// </summary>
    private const float AdobeTlCompensation = 1.02f;

    /// <summary>
    /// Stamp every overlay in <paramref name="overlays"/> onto <paramref name="page"/>.
    /// Overlays that fail (no insertion parent, glyph coverage impossible, IO error) are
    /// logged and skipped — a per-overlay failure doesn't take down the rest of the batch.
    /// </summary>
    internal static void Apply(PdfPage page, IReadOnlyList<AddParagraphOverlay>? overlays, WriterFontCache? fontCache,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (overlays is null || overlays.Count == 0) return;
        var log = logger ?? NullLogger.Instance;

        var doc = page.GetDocument();
        var root = doc.GetStructTreeRoot();
        int pageNumber = doc.GetPageNumber(page);
        var inv = PageFontInventory.Of(page);

        foreach (var overlay in overlays)
        {
            try
            {
                StampOne(page, doc, root, inv, pageNumber, overlay, fontCache, log);
            }
            catch (Exception e)
            {
                log.LogWarning("addParagraph overlay for new node {Id} on page {Page} failed: {Msg}",
                    overlay.NewNodeId, pageNumber, e.Message);
            }
        }
    }

    private static void StampOne(PdfPage page, PdfDocument doc, PdfStructTreeRoot root,
        PageFontInventory inv, int pageNumber,
        AddParagraphOverlay overlay, WriterFontCache? fontCache, ILogger log)
    {
        // ---- 1. Locate the parent StructElem via the donor sibling's MCR ---------------
        var insertionParent = FindInsertionParent(root, page, overlay.DonorMcid);
        if (insertionParent is null)
        {
            log.LogWarning("addParagraph {Id} on page {Page}: no StructElem found for donor MCID {Mcid} — skipping",
                overlay.NewNodeId, pageNumber, overlay.DonorMcid);
            return;
        }

        // ---- 2. Allocate a fresh MCID for this page ------------------------------------
        int newMcid = page.GetNextMcid();

        // ---- 3. Emit the tagged text block --------------------------------------------
        // Two-font resolution: preferred source-embedded font (for chars it can render)
        // plus a fallback (system/universal) for the leftover chars. Adobe reads the
        // paragraph's font from the first Tf op, so keeping source usage preserves the
        // source-font readout while still rendering out-of-subset chars via the fallback.
        var primary = ResolvePreferredSourceFont(inv, overlay);
        var fallback = ResolveFallbackFont(overlay, fontCache, log);
        var style = overlay.Style;
        // Doc-wide pool of same-family variants (Regular/Medium/Bold/etc. from other pages).
        var docPool = SortByStyleAffinity(
            PageFontInventory.DocCandidatesByFamilyAndWeight(doc, style.Family, style.Weight),
            style);

        // Prefer a SINGLE font that covers the whole text over a per-char split — Adobe's
        // Edit-PDF paragraph detection treats font switches inside a text block as
        // paragraph boundaries.
        PdfFont? wholeText = primary is not null && inv.CanRenderStrict(primary, overlay.NewContent)
            ? primary : null;
        if (wholeText is null)
        {
            foreach (var candidate in docPool)
            {
                if (ReferenceEquals(candidate, primary)) continue;
                if (inv.CanRenderStrict(candidate, overlay.NewContent))
                {
                    EnsureFontRegistered(page, candidate);
                    wholeText = candidate;
                    break;
                }
            }
        }
        var displayFont = wholeText ?? primary ?? fallback;
        var color = StandardFontMapper.ParseHex(style.ColorHex);
        float fontSize = style.Size >= 4.0f ? style.Size : 10.0f;

        var props = new PdfDictionary();
        props.Put(PdfName.MCID, new PdfNumber(newMcid));

        var role = MapRole(overlay.Tag);

        // Wrap-to-bbox using the display font's metrics so line breaks land where source's
        // typography would put them.
        var lines = ContentStreamMcidReplacer.WrapText(
            overlay.NewContent, displayFont, fontSize, (float)overlay.Width);
        float lineHeight = fontSize * ContentStreamMcidReplacer.EffectiveLeadingMultiplier(overlay.Style);

        // Anchor baselines from the bbox BOTTOM using the font's actual descender.
        int numLines = lines.Count;
        float descender = Math.Abs(displayFont.GetDescent(lines[numLines - 1], fontSize));
        if (!(descender > 0)) descender = fontSize * 0.2f;
        float emittedLineHeight = lineHeight * AdobeTlCompensation;
        float baselineY = (float)overlay.Y + descender + (numLines - 1) * emittedLineHeight;

        // Match source's emission pattern: Tf=1 with Tm.a=Tm.d=fontSize scaling, and
        // TL in text-space (leading_ratio) rather than user-space.
        float leadingRatio = lineHeight / fontSize;
        float emittedLeadingRatio = leadingRatio * AdobeTlCompensation;
        var canvas = new PdfCanvas(page, true);
        canvas.SaveState()
            .BeginMarkedContent(role, props)
            .SetFillColor(color)
            .BeginText()
            .SetFontAndSize(displayFont, 1f)
            .SetLeading(emittedLeadingRatio);
        float prevLineX = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float lineX = AlignedX(overlay, displayFont, fontSize, line);
            if (i == 0)
            {
                // Tm scales by fontSize on both axes; text-space (0,0) maps to
                // user-space (lineX, baselineY).
                canvas.SetTextMatrix(fontSize, 0, 0, fontSize, lineX, baselineY);
            }
            else if (Math.Abs(lineX - prevLineX) < 0.01f)
            {
                canvas.NewlineText();
            }
            else
            {
                // Td offsets are in TEXT-space; convert user-space delta X to text-space
                // by dividing by fontSize (Tm.a scale).
                canvas.MoveText((lineX - prevLineX) / fontSize, -emittedLeadingRatio);
            }
            prevLineX = lineX;
            if (wholeText is not null)
            {
                // Single-font emit — font already set once outside the loop.
                canvas.ShowText(line);
            }
            else
            {
                EmitLineWithFontSplit(canvas, line, primary, docPool, fallback, 1f, inv, page);
            }
        }
        canvas.EndText().EndMarkedContent().RestoreState();

        // ---- 4. Attach a StructElem + MCR under the insertion parent ------------------
        var newElem = new PdfStructElem(doc, role, page);
        insertionParent.AddKid(newElem);
        newElem.AddKid(new PdfMcrNumber(new PdfNumber(newMcid), newElem));

        log.LogDebug("addParagraph {Id} on page {Page}: emitted MCID {Mcid} under parent role {Role}",
            overlay.NewNodeId, pageNumber, newMcid,
            insertionParent.GetRole()?.GetValue() ?? "(root)");
    }

    /// <summary>
    /// DFS the tag tree for a <see cref="PdfMcr"/> whose (page, mcid) matches the donor.
    /// When found, the containing <see cref="PdfStructElem"/> is the donor sibling itself —
    /// the insertion parent is <i>that</i> element's parent.
    /// </summary>
    internal static PdfStructElem? FindInsertionParent(PdfStructTreeRoot root, PdfPage donorPage, int donorMcid)
    {
        if (donorMcid < 0) return null;
        return Walk(root, donorPage, donorMcid);
    }

    private static PdfStructElem? Walk(IStructureNode node, PdfPage donorPage, int donorMcid)
    {
        var kids = node.GetKids();
        if (kids is null) return null;
        foreach (var kid in kids)
        {
            if (kid is PdfMcr mcr)
            {
                if (mcr.GetMcid() == donorMcid && SameIndirect(mcr, donorPage))
                {
                    // The donor StructElem is `node`. Its parent is the insertion point.
                    var donorParent = node.GetParent();
                    return donorParent as PdfStructElem;
                }
            }
            else if (kid is PdfStructElem child)
            {
                var found = Walk(child, donorPage, donorMcid);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static bool SameIndirect(PdfMcr mcr, PdfPage page)
    {
        var mcrRef = mcr.GetPageIndirectReference();
        var pageRef = page.GetPdfObject().GetIndirectReference();
        return mcrRef is not null && mcrRef.Equals(pageRef);
    }

    /// <summary>
    /// Split <paramref name="line"/> into runs by which font can outline each char, then
    /// emit each run as its own <c>/Font size Tf (text)Tj</c> pair. Both fonts share the
    /// same current text matrix so runs draw at consecutive positions on the same baseline.
    /// </summary>
    private static void EmitLineWithFontSplit(PdfCanvas canvas, string line,
        PdfFont? primary, IReadOnlyList<PdfFont> docPool, PdfFont fallback,
        float fontSize, PageFontInventory inv, PdfPage page)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (primary is null)
        {
            canvas.ShowText(line);
            return;
        }
        var run = new StringBuilder();
        PdfFont? current = null;
        for (int i = 0; i < line.Length;)
        {
            int cp = char.ConvertToUtf32(line, i);
            var ch = char.ConvertFromUtf32(cp);
            var pick = PickFontForChar(ch, primary, docPool, fallback, inv, page);
            current ??= pick;
            if (!ReferenceEquals(pick, current))
            {
                canvas.SetFontAndSize(current, fontSize).ShowText(run.ToString());
                run.Clear();
                current = pick;
            }
            run.Append(ch);
            i += char.IsHighSurrogate(line[i]) ? 2 : 1;
        }
        if (run.Length > 0 && current is not null)
        {
            canvas.SetFontAndSize(current, fontSize).ShowText(run.ToString());
        }
    }

    /// <summary>
    /// Try <paramref name="primary"/>, then each doc-pool candidate (registered on the page's
    /// resources on first use), then <paramref name="fallback"/>.
    /// </summary>
    private static PdfFont PickFontForChar(string ch, PdfFont primary,
        IReadOnlyList<PdfFont> docPool, PdfFont fallback, PageFontInventory inv, PdfPage page)
    {
        if (inv.CanRenderStrict(primary, ch)) return primary;
        foreach (var candidate in docPool)
        {
            if (ReferenceEquals(candidate, primary)) continue;
            if (inv.CanRenderStrict(candidate, ch))
            {
                EnsureFontRegistered(page, candidate);
                return candidate;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Rank candidates by PostScript-name affinity to source's style so a Regular request
    /// prefers "…Regular" over "…Medium" / "…Bold". Stable — original order preserved within
    /// equal-score groups.
    /// </summary>
    private static IReadOnlyList<PdfFont> SortByStyleAffinity(IReadOnlyList<PdfFont> candidates, FontStyle style)
    {
        if (candidates.Count == 0) return candidates;
        var want = StyleSuffix(style.Family, style.Weight);
        var output = new List<PdfFont>(candidates);
        // Stable sort via index-based comparison.
        var indexed = output.Select((font, idx) => (font, idx))
            .OrderBy(t => AffinityScore(StyleSuffix(t.font.GetFontProgram().GetFontNames().GetFontName(), null), want))
            .ThenBy(t => t.idx)
            .Select(t => t.font)
            .ToList();
        return indexed;
    }

    private static string StyleSuffix(string? fontName, string? weightHint)
    {
        if (fontName is null)
        {
            return weightHint?.ToLowerInvariant() ?? string.Empty;
        }
        var stripped = fontName;
        int plus = stripped.IndexOf('+');
        if (plus >= 0 && plus <= 6) stripped = stripped[(plus + 1)..];
        int dash = stripped.IndexOf('-');
        var suffix = dash >= 0 ? stripped[(dash + 1)..] : string.Empty;
        if (suffix.Length == 0 && weightHint is not null) suffix = weightHint;
        return suffix.ToLowerInvariant();
    }

    private static int AffinityScore(string cand, string want)
    {
        if (string.Equals(cand, want, StringComparison.Ordinal)) return 0;
        if (cand.StartsWith(want, StringComparison.Ordinal) || want.StartsWith(cand, StringComparison.Ordinal)) return 1;
        return 2;
    }

    /// <summary>
    /// Add <paramref name="font"/> to the page's /Resources /Font dict if it isn't already
    /// there, so a <c>/Fn size Tf</c> op inside the added block can reference it.
    /// </summary>
    private static void EnsureFontRegistered(PdfPage page, PdfFont font)
    {
        try
        {
            page.GetResources().AddFont(page.GetDocument(), font);
        }
        catch
        {
            // Registration failure — canvas will still register lazily on SetFontAndSize.
        }
    }

    private static float AlignedX(AddParagraphOverlay overlay, PdfFont font, float fontSize, string lineText)
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
        return (float)overlay.X + bboxWidth - textWidth;
    }

    /// <summary>
    /// Source's embedded font twin (Benton, Verdana, etc.) that we'd like to use as the
    /// paragraph's primary display font. Uses tier 1 (exact by objNum) then tier 2 (family+
    /// weight from the page's inventory) with lenient canRender — the strict outline check
    /// is deferred to the per-char split.
    /// </summary>
    private static PdfFont? ResolvePreferredSourceFont(PageFontInventory inv, AddParagraphOverlay overlay)
    {
        var style = overlay.Style;
        if (style is null) return null;
        if (style.SourceFontObjNumber is { } objNum)
        {
            var exact = inv.FindByObjNumber(objNum);
            if (exact is not null) return exact;
        }
        if (!string.IsNullOrWhiteSpace(style.Family))
        {
            foreach (var candidate in inv.CandidatesByFamilyAndWeight(style.Family, style.Weight))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Fallback for chars the source font can't outline. System → universal → standard-14.
    /// </summary>
    private static PdfFont ResolveFallbackFont(AddParagraphOverlay overlay, WriterFontCache? fontCache, ILogger log)
    {
        var style = overlay.Style;
        if (style is not null && !string.IsNullOrWhiteSpace(style.Family))
        {
            var system = fontCache is not null
                ? fontCache.Load(style.Family, style.Weight)
                : SystemFontLocator.Load(style.Family, style.Weight);
            if (system is not null && PageFontInventory.CanRender(system, overlay.NewContent))
            {
                return system;
            }
        }
        var universal = fontCache is not null
            ? fontCache.LoadUniversalFallback(style)
            : SystemFontLocator.LoadUniversalFallback(style);
        if (universal is not null && PageFontInventory.CanRender(universal, overlay.NewContent))
        {
            return universal;
        }
        log.LogWarning("addParagraph {Id}: falling back to non-embedded standard-14 — will fail PDF/UA Font-embedding check",
            overlay.NewNodeId);
        return PdfFontFactory.CreateFont(StandardFontMapper.MapToStandardFont(style!));
    }

    private static PdfName MapRole(string? jsonRole) => jsonRole switch
    {
        null => PdfName.P,
        "P" => PdfName.P,
        "H1" => PdfName.H1,
        "H2" => PdfName.H2,
        "H3" => PdfName.H3,
        "H4" => PdfName.H4,
        "H5" => PdfName.H5,
        "H6" => PdfName.H6,
        "Lbl" => new PdfName(StandardRoles.LBL),
        "Span" => new PdfName(StandardRoles.SPAN),
        _ => PdfName.P
    };
}
