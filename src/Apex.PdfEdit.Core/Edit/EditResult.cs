namespace Apex.PdfEdit.Core.Edit;

public sealed class EditResult
{
    public EditPlan Plan { get; }
    public IReadOnlyList<string> AppliedOpIds { get; }
    public IReadOnlyList<EditIssue> Issues { get; }

    public EditResult(EditPlan plan, IEnumerable<string> appliedOpIds, IEnumerable<EditIssue> issues)
    {
        Plan = plan;
        AppliedOpIds = appliedOpIds.ToList().AsReadOnly();
        Issues = issues.ToList().AsReadOnly();
    }

    public bool IsOk => Issues.Count == 0;
}
