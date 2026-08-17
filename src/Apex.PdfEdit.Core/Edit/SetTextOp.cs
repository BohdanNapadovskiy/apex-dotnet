namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Change the text content of an existing tree node. The node's tag role, position,
/// font, and colour are preserved.
///
/// <see cref="Page"/> is optional — when set, the engine cross-checks it against the
/// resolved target's page and fails the op on mismatch.
/// </summary>
public sealed record SetTextOp(string Id, int? Page, string Target, string NewContent)
    : EditOp(Id, "setText")
{
    /// <summary>Compact factory for programmatic construction.</summary>
    public static SetTextOp Of(string id, string target, string newContent)
        => new(id, null, target, newContent);

    public static SetTextOp Of(string id, int? page, string target, string newContent)
        => new(id, page, target, newContent);
}
