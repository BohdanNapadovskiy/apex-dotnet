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
/// Draws a new list item — <c>LI { Lbl, LBody }</c> — under an existing <c>L</c>
/// container. Companion to <see cref="AddParagraphStamper"/>, differs in two respects:
/// <list type="bullet">
///   <item><b>Walk-up depth</b>: the donor MCID belongs to the donor LI's Lbl or LBody,
///       so the insertion parent (the L) is <i>two</i> struct levels up from that MCR.</item>
///   <item><b>Compound structure</b>: three new StructElems (LI + Lbl + LBody) and two
///       new MCIDs are minted per overlay so that a single addListItem op produces a
///       well-tagged pair.</item>
/// </list>
/// </summary>
internal static class AddListItemStamper
{
    /// <summary>See <see cref="AddParagraphStamper"/>'s AdobeTlCompensation for rationale.</summary>
    private const float AdobeTlCompensation = 1.02f;

    internal static void Apply(PdfPage page, IReadOnlyList<AddListItemOverlay>? overlays, WriterFontCache? fontCache,
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
                log.LogWarning("addListItem overlay for new LI {Id} on page {Page} failed: {Msg}",
                    overlay.NewLiId, pageNumber, e.Message);
            }
        }
    }

    private static void StampOne(PdfPage page, PdfDocument doc, PdfStructTreeRoot root,
        PageFontInventory inv, int pageNumber, AddListItemOverlay overlay, WriterFontCache? fontCache, ILogger log)
    {
        // ---- 1. Walk from donor MCID up two levels to find the L container -------------
        var listContainer = FindListContainer(root, page, overlay.DonorLiMcid);
        if (listContainer is null)
        {
            log.LogWarning("addListItem {Id} on page {Page}: no L StructElem found via donor MCID {Mcid} — skipping",
                overlay.NewLiId, pageNumber, overlay.DonorLiMcid);
            return;
        }

        // ---- 2. Allocate two fresh MCIDs for Lbl and LBody -----------------------------
        int mcidLbl = page.GetNextMcid();
        int mcidBody = page.GetNextMcid();

        // ---- 3. Emit the two tagged text blocks in one canvas pass --------------------
        // Resolve font / colour / size / leading INDEPENDENTLY per column — bullets often
        // differ from body text in size and colour.
        var lblFont = ResolveFontFor(inv, overlay.LblStyle, overlay.LabelText, overlay.NewLiId, fontCache, log);
        var lblColor = StandardFontMapper.ParseHex(overlay.LblStyle.ColorHex);
        float lblFontSize = overlay.LblStyle.Size >= 4.0f ? overlay.LblStyle.Size : 10.0f;
        float lblLeadingMult = ContentStreamMcidReplacer.EffectiveLeadingMultiplier(overlay.LblStyle);

        var bodyFont = ResolveFontFor(inv, overlay.BodyStyle, overlay.BodyText, overlay.NewLiId, fontCache, log);
        var bodyColor = StandardFontMapper.ParseHex(overlay.BodyStyle.ColorHex);
        float bodyFontSize = overlay.BodyStyle.Size >= 4.0f ? overlay.BodyStyle.Size : 10.0f;
        float bodyLeadingMult = ContentStreamMcidReplacer.EffectiveLeadingMultiplier(overlay.BodyStyle);

        var canvas = new PdfCanvas(page, true);
        StampTextBlock(canvas, lblFont, lblColor, lblFontSize, lblLeadingMult,
            new PdfName(StandardRoles.LBL), mcidLbl,
            overlay.LabelText,
            (float)overlay.LblX, (float)overlay.LblY,
            (float)overlay.LblWidth, (float)overlay.LblHeight,
            Alignment.Left); // labels always left-anchor
        StampTextBlock(canvas, bodyFont, bodyColor, bodyFontSize, bodyLeadingMult,
            new PdfName(StandardRoles.LBODY), mcidBody,
            overlay.BodyText,
            (float)overlay.BodyX, (float)overlay.BodyY,
            (float)overlay.BodyWidth, (float)overlay.BodyHeight,
            overlay.Alignment);

        // ---- 4. Attach LI + Lbl + LBody StructElems -----------------------------------
        var newLi = new PdfStructElem(doc, PdfName.LI, page);
        listContainer.AddKid(newLi);

        var newLbl = new PdfStructElem(doc, new PdfName(StandardRoles.LBL), page);
        newLi.AddKid(newLbl);
        newLbl.AddKid(new PdfMcrNumber(new PdfNumber(mcidLbl), newLbl));

        var newLBody = new PdfStructElem(doc, new PdfName(StandardRoles.LBODY), page);
        newLi.AddKid(newLBody);
        newLBody.AddKid(new PdfMcrNumber(new PdfNumber(mcidBody), newLBody));

        // ---- 5. Layout attributes so single-line items report source's line spacing -----
        // Adobe derives line spacing from baseline-to-baseline distance; single-line falls
        // back to Adobe's 1.20 default. /Layout attributes with /LineHeight makes readers
        // that consult tag attributes report source's spacing regardless of line count.
        float bodyLineHeight = bodyFontSize * bodyLeadingMult;
        AttachLineHeight(newLBody, bodyLineHeight);
        float lblLineHeight = lblFontSize * lblLeadingMult;
        AttachLineHeight(newLbl, lblLineHeight);

        log.LogDebug("addListItem {Id} on page {Page}: emitted LI with Lbl mcid={Lbl} + LBody mcid={Body} under L",
            overlay.NewLiId, pageNumber, mcidLbl, mcidBody);
    }

    /// <summary>One BDC/EMC block: tagged marked-content wrapping a single text-object draw.</summary>
    private static void StampTextBlock(PdfCanvas canvas, PdfFont font, Color color, float fontSize,
        float leadingMult, PdfName role, int mcid, string text,
        float x, float y, float width, float height,
        Alignment alignment)
    {
        var props = new PdfDictionary();
        props.Put(PdfName.MCID, new PdfNumber(mcid));

        var lines = ContentStreamMcidReplacer.WrapText(text, font, fontSize, width);
        float lineHeight = fontSize * leadingMult;
        float emittedLineHeight = lineHeight * AdobeTlCompensation;
        // Anchor the FIRST baseline near the TOP of the reserved bbox — the engine
        // reserves height for its char-advance-based line estimate which often over-
        // predicts by one line for wide columns. Top-anchoring keeps the item flush
        // with the previous LI regardless of the estimate's overshoot.
        float baselineY = height > 0
            ? y + height - fontSize * 0.75f
            : y + fontSize * 0.25f;

        canvas.SaveState()
            .BeginMarkedContent(role, props)
            .SetFillColor(color)
            .BeginText()
            .SetFontAndSize(font, fontSize)
            .SetLeading(emittedLineHeight);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float lineX = AlignedX(x, width, font, fontSize, line, alignment);
            float lineY = baselineY - i * emittedLineHeight;
            canvas.SetTextMatrix(lineX, lineY).ShowText(line);
        }
        canvas.EndText().EndMarkedContent().RestoreState();
    }

    private static void AttachLineHeight(PdfStructElem elem, float lineHeight)
    {
        if (!(lineHeight > 0)) return;
        var attrs = new PdfStructureAttributes("Layout");
        attrs.AddFloatAttribute("LineHeight", lineHeight);
        elem.SetAttributes(attrs.GetPdfObject());
    }

    private static float AlignedX(float x, float width, PdfFont font, float fontSize,
        string lineText, Alignment a)
    {
        if (a == Alignment.Left || a == Alignment.Justified || a == Alignment.Unknown)
        {
            return x;
        }
        float textWidth = font.GetWidth(lineText, fontSize);
        if (a == Alignment.Center) return x + (width - textWidth) / 2f;
        return x + width - textWidth; // Right
    }

    /// <summary>
    /// DFS the tag tree for the donor MCR, then walk up two struct levels — MCR's containing
    /// StructElem is the donor Lbl/LBody, its parent is the donor LI, and the LI's parent is
    /// the L we insert into.
    /// </summary>
    internal static PdfStructElem? FindListContainer(PdfStructTreeRoot root, PdfPage donorPage, int donorMcid)
    {
        if (donorMcid < 0) return null;
        var donorLeaf = Walk(root, donorPage, donorMcid);
        if (donorLeaf is null) return null;
        var donorLi = donorLeaf.GetParent();
        if (donorLi is not PdfStructElem li) return null;
        var listContainer = li.GetParent();
        return listContainer as PdfStructElem;
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
                    var parent = node.GetParent();
                    return (node as PdfStructElem) ?? (parent as PdfStructElem);
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
    /// Font resolution — tries the source PDF's own embedded subset first, then system,
    /// then universal fallback, then standard-14. Embedded-first is critical for symbol/
    /// dingbat labels: source bullets are commonly Wingdings/Symbol glyphs at PUA
    /// codepoints that no system Latin font can render, but the source PDF's own subset
    /// has the exact glyph.
    /// </summary>
    private static PdfFont ResolveFontFor(PageFontInventory inv, FontStyle style, string text,
        string newLiId, WriterFontCache? fontCache, ILogger log)
    {
        // Tier 1: exact source-font twin by PDF object number (strict check for subset outline).
        if (style.SourceFontObjNumber is { } objNum)
        {
            var exact = inv.FindByObjNumber(objNum);
            if (exact is not null && inv.CanRenderStrict(exact, text))
            {
                return exact;
            }
        }
        // Tier 2: family+weight candidates already embedded on this page (strict check).
        if (!string.IsNullOrWhiteSpace(style.Family))
        {
            foreach (var candidate in inv.CandidatesByFamilyAndWeight(style.Family, style.Weight))
            {
                if (inv.CanRenderStrict(candidate, text)) return candidate;
            }
        }
        // Tier 3: fresh system font.
        if (!string.IsNullOrWhiteSpace(style.Family))
        {
            var system = fontCache is not null
                ? fontCache.Load(style.Family, style.Weight)
                : SystemFontLocator.Load(style.Family, style.Weight);
            if (system is not null && PageFontInventory.CanRender(system, text))
            {
                return system;
            }
        }
        // Tier 4: universal serif/mono/default fallback.
        var universal = fontCache is not null
            ? fontCache.LoadUniversalFallback(style)
            : SystemFontLocator.LoadUniversalFallback(style);
        if (universal is not null && PageFontInventory.CanRender(universal, text))
        {
            return universal;
        }
        log.LogWarning("addListItem {Id}: falling back to non-embedded standard-14 for '{Text}' — will fail PDF/UA Font-embedding check",
            newLiId, text.Length > 30 ? text[..30] + "..." : text);
        return PdfFontFactory.CreateFont(StandardFontMapper.MapToStandardFont(style));
    }
}
