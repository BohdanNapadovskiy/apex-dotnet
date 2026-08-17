using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Tagging;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Snapshot of every source StructElem's accessibility attributes (<c>/Alt</c>,
/// <c>/ActualText</c>, <c>/Lang</c>), keyed so the destination writer can find
/// the source attrs for the freshly-added dest tag it just created.
///
/// Key: <c>(page, canonicalMcid, role)</c> where canonicalMcid is the MCID of the
/// first descendant marked-content reference of the source StructElem in DFS order
/// (i.e. the first MCID a screen-reader would reach walking that subtree). This
/// gives a positional key that survives extractor-side flattening.
///
/// Role is included because the same (page, canonicalMcid) pair appears at multiple
/// levels in the ancestor chain (e.g. Sect &gt; P &gt; Span all descend to the same
/// leaf MCID), and their /Alt values differ.
///
/// Only StructElems with at least one non-empty attribute are indexed — everything
/// else would be dead weight in the map and add ambiguity if two ancestors share a
/// canonical MCID and one has attrs and the other doesn't.
/// </summary>
internal sealed class SourceStructAttrIndex
{
    /// <summary>
    /// One StructElem's copied attributes. All fields nullable — <see cref="IsEmpty"/>
    /// returns true when nothing is worth propagating.
    /// </summary>
    internal sealed record Attrs(string? Alt, string? ActualText, string? Lang)
    {
        internal bool IsEmpty() => IsBlank(Alt) && IsBlank(ActualText) && IsBlank(Lang);
        private static bool IsBlank(string? s) => string.IsNullOrEmpty(s);
    }

    private readonly Dictionary<string, Attrs> _byKey = new();

    /// <summary>
    /// Source annotation dicts (Link widgets, Form field widgets) keyed by the
    /// same (page, canonicalMcid, role) as <see cref="_byKey"/>. Populated from
    /// <see cref="PdfObjRef"/> children while walking the source StructElem tree.
    /// The OCR writer copies these dicts onto dest pages and emits fresh OBJRs so
    /// the rebuilt tag tree carries the same clickable Links / form widgets as
    /// source.
    /// </summary>
    private readonly Dictionary<string, PdfDictionary> _annotByKey = new();

    /// <summary>
    /// Build an index by walking every StructElem in the source's StructTreeRoot.
    /// Safe to call on a source that has no tag tree — returns an empty index that
    /// <see cref="Lookup"/> always misses.
    /// </summary>
    internal static SourceStructAttrIndex Build(PdfDocument? source)
    {
        var idx = new SourceStructAttrIndex();
        if (source is null) return idx;
        var root = source.GetStructTreeRoot();
        if (root is null) return idx;
        foreach (var kid in root.GetKids())
        {
            if (kid is PdfStructElem se)
            {
                idx.Walk(se, source);
            }
        }
        return idx;
    }

    /// <summary>
    /// DFS the source StructElem tree. Returns the canonical (page, mcid) of the
    /// first <i>visible</i> MCID reachable in this subtree (mcid ≥ 0), or -1 if the
    /// subtree has no visible MCID descendant. Attrs are indexed only when non-empty
    /// AND the subtree has a canonical MCID.
    ///
    /// OBJR marked-content refs (mcid = -1, pointing at annotations like Link widgets)
    /// are deliberately skipped — the dest side's <c>canonicalMcidOf(TreeNode)</c>
    /// walks children by <c>node.Mcid ≥ 0</c> only, so the source index must key off
    /// the same "first visible MCID" semantics or lookups miss.
    /// </summary>
    private long Walk(PdfStructElem se, PdfDocument source)
    {
        long canonical = -1L;
        PdfDictionary? annotDict = null;
        foreach (var kid in se.GetKids())
        {
            if (kid is PdfStructElem child)
            {
                long c = Walk(child, source);
                if (canonical == -1L && c != -1L) canonical = c;
            }
            else if (kid is PdfObjRef objRef)
            {
                // OBJR checked BEFORE PdfMcr because PdfObjRef extends PdfMcr —
                // "is PdfMcr" would match too and skip the annotation capture.
                if (annotDict is null) annotDict = objRef.GetReferencedObject();
            }
            else if (kid is PdfMcr mcr)
            {
                int mcid = mcr.GetMcid();
                if (canonical == -1L && mcid >= 0)
                {
                    int page = PageNumber(source, mcr.GetPageObject());
                    if (page > 0) canonical = PackKey(page, mcid);
                }
            }
        }
        var a = new Attrs(PdfStr(se.GetAlt()), PdfStr(se.GetActualText()), PdfStr(se.GetLang()));
        var role = se.GetRole()?.GetValue() ?? string.Empty;
        if (!a.IsEmpty() && canonical != -1L)
        {
            _byKey.TryAdd(MapKey(canonical, role), a);
        }
        if (annotDict is not null && canonical != -1L)
        {
            _annotByKey.TryAdd(MapKey(canonical, role), annotDict);
        }
        return canonical;
    }

    /// <summary>
    /// Look up the source annotation dict associated with a dest tag about to be
    /// added at position (page, canonicalMcid, role). Returns null when no source
    /// annotation is registered (typical for plain P/H tags).
    /// </summary>
    internal PdfDictionary? LookupAnnotation(int page, int canonicalMcid, string? role)
    {
        if (page < 1 || canonicalMcid < 0 || role is null) return null;
        return _annotByKey.TryGetValue(MapKey(PackKey(page, canonicalMcid), role), out var v) ? v : null;
    }

    /// <summary>
    /// Look up copied attrs for a dest tag about to be added at position
    /// (page, canonicalMcid) with role role. Returns null when no source StructElem matches.
    /// </summary>
    internal Attrs? Lookup(int page, int canonicalMcid, string? role)
    {
        if (page < 1 || canonicalMcid < 0 || role is null) return null;
        return _byKey.TryGetValue(MapKey(PackKey(page, canonicalMcid), role), out var v) ? v : null;
    }

    private static long PackKey(int page, int mcid) => ((long)page << 32) | (uint)mcid;

    private static string MapKey(long pageMcid, string role) => $"{pageMcid}|{role}";

    /// <summary>
    /// Resolve a source page dictionary to its 1-based page number. Returns 0 on
    /// mismatch (page object not owned by this doc — shouldn't happen for MCRs
    /// produced by walking the same doc's struct tree, but defensive).
    /// </summary>
    private static int PageNumber(PdfDocument source, PdfDictionary? pageObj)
    {
        if (pageObj is null) return 0;
        int count = source.GetNumberOfPages();
        for (int p = 1; p <= count; p++)
        {
            if (source.GetPage(p).GetPdfObject() == pageObj) return p;
        }
        return 0;
    }

    private static string? PdfStr(PdfString? s) => s?.ToUnicodeString();
}
