using System.Globalization;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Tagging;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Compares two tagged PDFs' structure trees by walking every StructElem's marked-content
/// descendants and diffing role + parent-role at the (page, mcid) level. Used for the OCR
/// follow-ups: "walk source PDF's tag tree, dump; walk edited PDF's tag tree; diff; every
/// remaining mismatch is a bug".
///
/// Categories reported:
/// <list type="bullet">
///   <item><see cref="MismatchCategory.Missing"/> — MCID present in source, absent in edited.</item>
///   <item><see cref="MismatchCategory.Added"/> — MCID present in edited, absent in source
///       (typically a legal addParagraph/addListItem outcome; call site must filter).</item>
///   <item><see cref="MismatchCategory.RoleMismatch"/> — MCID present in both but the leaf StructElem role differs.</item>
///   <item><see cref="MismatchCategory.ParentMismatch"/> — MCID present in both, roles match, but the immediate parent role differs.</item>
/// </list>
/// </summary>
public static class TagTreeDiff
{
    /// <summary>
    /// Open both PDFs read-only and diff their tag trees. Returns an empty
    /// <see cref="DiffResult"/> when both are identity-equivalent at the (page, mcid, role, parent)
    /// level. Neither PDF is mutated.
    /// </summary>
    public static DiffResult Diff(string source, string edited)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edited);
        Dictionary<Key, Entry> srcMap;
        Dictionary<Key, Entry> dstMap;
        using (var r = new PdfReader(source))
        using (var d = new PdfDocument(r))
        {
            srcMap = IndexBy(d);
        }
        using (var r = new PdfReader(edited))
        using (var d = new PdfDocument(r))
        {
            dstMap = IndexBy(d);
        }
        var issues = new List<Mismatch>();
        var allKeys = new LinkedHashSet<Key>();
        foreach (var k in srcMap.Keys) allKeys.Add(k);
        foreach (var k in dstMap.Keys) allKeys.Add(k);
        foreach (var k in allKeys)
        {
            srcMap.TryGetValue(k, out var s);
            dstMap.TryGetValue(k, out var e);
            if (s is null)
            {
                issues.Add(new Mismatch(MismatchCategory.Added, k.Page, k.Mcid, null, null, e!.Role, e.ParentRole));
            }
            else if (e is null)
            {
                issues.Add(new Mismatch(MismatchCategory.Missing, k.Page, k.Mcid, s.Role, s.ParentRole, null, null));
            }
            else if (!string.Equals(s.Role, e.Role, StringComparison.Ordinal))
            {
                issues.Add(new Mismatch(MismatchCategory.RoleMismatch, k.Page, k.Mcid,
                    s.Role, s.ParentRole, e.Role, e.ParentRole));
            }
            else if (!string.Equals(s.ParentRole, e.ParentRole, StringComparison.Ordinal))
            {
                issues.Add(new Mismatch(MismatchCategory.ParentMismatch, k.Page, k.Mcid,
                    s.Role, s.ParentRole, e.Role, e.ParentRole));
            }
        }
        return new DiffResult(issues, srcMap.Count, dstMap.Count);
    }

    /// <summary>
    /// Index every visible marked-content reference in the doc by (page, mcid) →
    /// leaf StructElem role + immediate-parent StructElem role. Skips MCRs that lack a
    /// page or a numeric MCID.
    /// </summary>
    private static Dictionary<Key, Entry> IndexBy(PdfDocument doc)
    {
        var output = new Dictionary<Key, Entry>();
        var root = doc.GetStructTreeRoot();
        if (root is null) return output;
        foreach (var kid in root.GetKids())
        {
            if (kid is PdfStructElem se) Walk(se, doc, output);
        }
        return output;
    }

    private static void Walk(PdfStructElem se, PdfDocument doc, Dictionary<Key, Entry> output)
    {
        var role = RoleOf(se);
        var parentRole = RoleOfParent(se);
        foreach (var child in se.GetKids())
        {
            if (child is PdfStructElem childSe)
            {
                Walk(childSe, doc, output);
            }
            else if (child is PdfMcr mcr)
            {
                int mcid = mcr.GetMcid();
                int page = PageNumberOf(mcr, doc);
                if (mcid < 0 || page < 1) continue;
                output[new Key(page, mcid)] = new Entry(role, parentRole);
            }
        }
    }

    private static string RoleOf(PdfStructElem se)
    {
        var role = se.GetRole();
        return role is null ? string.Empty : role.GetValue();
    }

    private static string RoleOfParent(PdfStructElem se)
    {
        var parent = se.GetParent();
        if (parent is PdfStructElem parentSe) return RoleOf(parentSe);
        return string.Empty;
    }

    /// <summary>
    /// iText exposes the page via the MCR's page-object dictionary — resolve it by
    /// scanning the document's pages once. O(N × pageCount), fine at POC scale.
    /// </summary>
    private static int PageNumberOf(PdfMcr mcr, PdfDocument doc)
    {
        var pageObj = mcr.GetPageObject();
        if (pageObj is null) return -1;
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            if (doc.GetPage(i).GetPdfObject() == pageObj) return i;
        }
        return -1;
    }

    /// <summary>
    /// Insertion-ordered set of keys — Java's LinkedHashSet analog. Standard
    /// <see cref="HashSet{T}"/> doesn't guarantee iteration order.
    /// </summary>
    private sealed class LinkedHashSet<T> where T : notnull
    {
        private readonly Dictionary<T, byte> _members = new();
        private readonly List<T> _order = new();

        internal void Add(T value)
        {
            if (_members.TryAdd(value, 0))
            {
                _order.Add(value);
            }
        }

        public IEnumerator<T> GetEnumerator() => _order.GetEnumerator();
    }

    private sealed record Key(int Page, int Mcid);
    private sealed record Entry(string Role, string ParentRole);
}

/// <summary>
/// Outcome category of a single mismatched (page, mcid) key.
/// </summary>
public enum MismatchCategory
{
    Missing,
    Added,
    RoleMismatch,
    ParentMismatch
}

/// <summary>
/// One mismatch between source's and edited's tag trees at a (page, mcid) key.
/// </summary>
public sealed record Mismatch(
    MismatchCategory Category,
    int Page,
    int Mcid,
    string? SourceRole,
    string? SourceParentRole,
    string? EditedRole,
    string? EditedParentRole)
{
    public override string ToString() => Category switch
    {
        MismatchCategory.Missing => string.Format(CultureInfo.InvariantCulture,
            "MISSING          page={0} mcid={1}  source={2} (parent={3})",
            Page, Mcid, SourceRole, SourceParentRole),
        MismatchCategory.Added => string.Format(CultureInfo.InvariantCulture,
            "ADDED            page={0} mcid={1}  edited={2} (parent={3})",
            Page, Mcid, EditedRole, EditedParentRole),
        MismatchCategory.RoleMismatch => string.Format(CultureInfo.InvariantCulture,
            "ROLE_MISMATCH    page={0} mcid={1}  source={2} edited={3} (parent source={4} edited={5})",
            Page, Mcid, SourceRole, EditedRole, SourceParentRole, EditedParentRole),
        MismatchCategory.ParentMismatch => string.Format(CultureInfo.InvariantCulture,
            "PARENT_MISMATCH  page={0} mcid={1}  role={2}  parent source={3} edited={4}",
            Page, Mcid, SourceRole, SourceParentRole, EditedParentRole),
        _ => base.ToString() ?? string.Empty
    };
}

/// <summary>Result of <see cref="TagTreeDiff.Diff"/> — mismatches + summary counts.</summary>
public sealed record DiffResult(
    IReadOnlyList<Mismatch> Mismatches,
    int SourceMcidCount,
    int EditedMcidCount)
{
    public int MissingCount => CountOf(MismatchCategory.Missing);
    public int AddedCount => CountOf(MismatchCategory.Added);
    public int RoleMismatchCount => CountOf(MismatchCategory.RoleMismatch);
    public int ParentMismatchCount => CountOf(MismatchCategory.ParentMismatch);
    public bool Clean => Mismatches.Count == 0;

    private int CountOf(MismatchCategory c) => Mismatches.Count(m => m.Category == c);
}
