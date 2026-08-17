namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Insert a new list item (<c>LI</c>) under an existing <c>L</c> container, atomically
/// creating the <c>Lbl</c> + <c>LBody</c> pair that a well-tagged list item requires.
///
/// Unlike <see cref="AddParagraphOp"/>, placement is <i>list-aware</i>: the new Lbl inherits
/// the donor LI's Lbl X/width (so bullets/numbers stay aligned across the list) and the new
/// LBody inherits the donor LI's LBody X/width. Both share the same Y (baseline of the new row).
///
/// <see cref="Parent"/> is the <c>L</c> container's id. <see cref="Index"/> selects insertion
/// position among the L's existing LI children (-1 = append). <see cref="Style"/>'s
/// InheritFrom may point at the donor LI or at either of its Lbl/LBody children.
///
/// <see cref="Page"/> is optional — when set, the engine cross-checks it against the page.
/// </summary>
public sealed record AddListItemOp(
    string Id,
    int? Page,
    string Parent,
    int Index,
    string LabelText,
    string BodyText,
    StyleSpec? Style)
    : EditOp(Id, "addListItem")
{
    public static AddListItemOp Of(string id, string parent, int index,
        string labelText, string bodyText, StyleSpec? style)
        => new(id, null, parent, index, labelText, bodyText, style);

    public static AddListItemOp Of(string id, int? page, string parent, int index,
        string labelText, string bodyText, StyleSpec? style)
        => new(id, page, parent, index, labelText, bodyText, style);
}
