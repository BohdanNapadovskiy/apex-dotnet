namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Insert a new paragraph. Blocked on the customer's §5.5 reflow rule for POC.
///
/// <see cref="Page"/> is optional — when set, the engine cross-checks it against the
/// page it resolves from the position donor and fails the op on mismatch.
/// </summary>
public sealed record AddParagraphOp(
    string Id,
    int? Page,
    string Parent,
    int Index,
    string Tag,
    string Content,
    StyleSpec? Style)
    : EditOp(Id, "addParagraph")
{
    public static AddParagraphOp Of(string id, string parent, int index, string tag,
        string content, StyleSpec? style)
        => new(id, null, parent, index, tag, content, style);

    public static AddParagraphOp Of(string id, int? page, string parent, int index, string tag,
        string content, StyleSpec? style)
        => new(id, page, parent, index, tag, content, style);
}
