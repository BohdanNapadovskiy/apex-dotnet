using Apex.PdfEdit.Core.Edit;
using iText.Kernel.Pdf;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Writer that uses the source PDF as a visual base. Opens the source in <b>stamp mode</b>
/// — a single <see cref="PdfDocument"/> bound to both a <see cref="PdfReader"/> (source)
/// and a <see cref="PdfWriter"/> (dest stream) — and applies overlays in place. When the
/// document closes, iText serialises the mutated file to <c>output</c>, preserving widgets,
/// annotations, embedded fonts, images, form fields, and — critically — the source's
/// PDF/UA-compliant tag tree with its ParentTree back-references intact.
///
/// This is a fundamental switch from an earlier <c>src.CopyPagesTo(dst)</c> two-document
/// architecture. <c>CopyPagesTo</c> — even wrapped in <c>PdfMerger</c> — rewires the
/// destination's StructTreeRoot and ParentTree imperfectly, leaving thousands of "Text
/// object not tagged" and "Structural parent tree" PAC errors on identity pass-through of
/// a fully-compliant source. Stamp mode sidesteps that copy step entirely.
///
/// Contrast with <see cref="FontAwareWriter"/>, which rebuilds pages from JSON only —
/// that model can't reconstruct non-text page content (widgets, barcodes, seals, rules)
/// because none of it exists in <c>document.json</c> or <c>geometry.json</c>. Under
/// source-as-base, the JSON pair remains the source of truth for the structure and content
/// of tagged <i>text</i>; the source PDF supplies everything else.
/// </summary>
public sealed class SourceBasedWriter
{
    private readonly string _sourcePdf;

    public SourceBasedWriter(string sourcePdf)
    {
        _sourcePdf = sourcePdf;
    }

    /// <summary>Identity pass-through — no overlays applied.</summary>
    public void Write(Stream output) => Write(EditPlan.Empty(), output);

    /// <summary>
    /// Open the source in stamp mode and apply the plan's overlays in place. No page
    /// copying: the source's tag tree, ParentTree, catalog metadata, and form fields all
    /// survive the write untouched. When the <see cref="PdfDocument"/> closes, iText
    /// serialises the mutated file to <paramref name="output"/>.
    /// </summary>
    public void Write(EditPlan plan, Stream output)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(output);

        using var reader = new PdfReader(_sourcePdf);
        using var writer = new PdfWriter(output, WriterDeterminism.PropsForSource(_sourcePdf));
        using var doc = new PdfDocument(reader, writer, new StampingProperties());

        // Shared font cache — one PdfFont per (family, weight) per write pass so
        // multiple overlays don't each embed their own subset of the same base font.
        var fontCache = new WriterFontCache();

        int pageCount = doc.GetNumberOfPages();

        // ---- delete overlays: drop target BDC/EMC blocks + prune tag tree --------------
        // Runs FIRST so downstream passes (setText, move, add) never reference an
        // MCID we're about to remove.
        var deleteByPage = GroupByPage(plan.DeleteOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in deleteByPage)
        {
            ContentStreamMcidDeleter.Apply(doc.GetPage(page), list);
        }

        // ---- setText overlays: rewrite each target page's content stream ---------------
        var setByPage = GroupByPage(plan.SetTextOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in setByPage)
        {
            ContentStreamMcidReplacer.Apply(doc.GetPage(page), list, fontCache);
        }

        // ---- move overlays: shift target BDC/EMC blocks by (dx, dy) --------------------
        // Runs after setText because both rewrite /Contents on the same page.
        var moveByPage = GroupByPage(plan.MoveOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in moveByPage)
        {
            ContentStreamMcidMover.Apply(doc.GetPage(page), list);
        }

        // ---- path-band overlays: shift/grow untagged rectangles so decorations move
        // with the pushed-down content instead of clipping it. Runs AFTER the MCID
        // mover so both passes see a stable snapshot of /Contents.
        var pathByPage = GroupByPage(plan.PathBandOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in pathByPage)
        {
            ContentStreamPathBandShifter.Apply(doc.GetPage(page), list);
        }

        // ---- addParagraph overlays: stamp fresh tagged blocks --------------------------
        // Runs after mutation passes so their rewrites are already committed to /Contents
        // before the stamper appends its new content stream.
        var addByPage = GroupByPage(plan.AddParagraphOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in addByPage)
        {
            AddParagraphStamper.Apply(doc.GetPage(page), list, fontCache);
        }

        // ---- addListItem overlays: stamp new LI (Lbl + LBody) under an existing L ------
        // Independent from addParagraph (different parent roles) but ordering the
        // compound op last keeps the write pipeline simple.
        var listByPage = GroupByPage(plan.AddListItemOverlays, static o => o.Page, pageCount);
        foreach (var (page, list) in listByPage)
        {
            AddListItemStamper.Apply(doc.GetPage(page), list, fontCache);
        }

        // ---- Link /Contents backfill: PDF/UA-1 §7.18 requires Link annotations to
        // carry accessibility text; most source PDFs omit it. Derived from the Link's
        // action dict. Runs last so it sees the final annotation set.
        LinkContentsFiller.ApplyAll(doc);

        // ---- Form-field /TU backfill: PDF/UA-1 §7.18.5 requires Widget annotations
        // to expose an accessible name; most source PDFs omit /TU and don't carry
        // alt-text on their Form StructElem either. Derived from the field's
        // fully-qualified name via the /Parent chain.
        FormFieldTuFiller.ApplyAll(doc);

        // ---- Orphan-widget tagger: PDF/UA-1 §7.18.4 requires every Widget annotation
        // to be reachable via a Form StructElem's OBJR (or explicitly artifact-marked).
        // Sources sometimes emit widgets whose Form tag was never authored; wrap the
        // orphan in a fresh Form under the tree root so the checker is satisfied.
        OrphanWidgetTagger.ApplyAll(doc);
    }

    /// <summary>
    /// Bucket overlays by page number, skipping any that fall outside the doc's page
    /// range. Preserves original overlay order within each bucket.
    /// </summary>
    private static Dictionary<int, List<T>> GroupByPage<T>(IReadOnlyList<T> overlays, Func<T, int> pageOf, int pageCount)
    {
        var byPage = new Dictionary<int, List<T>>();
        foreach (var o in overlays)
        {
            int page = pageOf(o);
            if (page < 1 || page > pageCount) continue;
            if (!byPage.TryGetValue(page, out var list))
            {
                list = new List<T>();
                byPage[page] = list;
            }
            list.Add(o);
        }
        return byPage;
    }
}
