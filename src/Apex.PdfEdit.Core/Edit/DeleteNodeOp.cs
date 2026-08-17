namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Delete a node and its subtree. Blocked on §5.5 reflow rule for POC.
///
/// <see cref="Page"/> is optional — when set, the engine cross-checks it against the
/// resolved target's page and fails the op on mismatch.
/// </summary>
public sealed record DeleteNodeOp(string Id, int? Page, string Target)
    : EditOp(Id, "deleteNode")
{
    public static DeleteNodeOp Of(string id, string target)
        => new(id, null, target);

    public static DeleteNodeOp Of(string id, int? page, string target)
        => new(id, page, target);
}
