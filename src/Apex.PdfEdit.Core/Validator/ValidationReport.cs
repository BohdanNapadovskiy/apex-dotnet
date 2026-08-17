namespace Apex.PdfEdit.Core.Validator;

public sealed class ValidationReport
{
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public ValidationReport(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues.ToList().AsReadOnly();
    }

    public bool IsClean() => !Issues.Any(i => i.Severity == Severity.Error);

    public long ErrorCount() => Issues.Count(i => i.Severity == Severity.Error);

    public long WarningCount() => Issues.Count(i => i.Severity == Severity.Warning);

    public IReadOnlyList<ValidationIssue> Errors()
        => Issues.Where(i => i.Severity == Severity.Error).ToList();

    /// <summary>
    /// Count of distinct node ids that have at least one ERROR issue. Matches the
    /// per-node violation count reported by <c>_validator_baseline.py</c>.
    /// </summary>
    public long DistinctErrorNodes()
        => Issues.Where(i => i.Severity == Severity.Error)
                 .Select(i => i.NodeId)
                 .Distinct()
                 .LongCount();
}
