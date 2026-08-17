namespace Apex.PdfEdit.Core.Validator;

public sealed record ValidationIssue(
    Severity Severity,
    string Code,
    string NodeId,
    string Message)
{
    public static ValidationIssue Error(string code, string nodeId, string message)
        => new(Severity.Error, code, nodeId, message);

    public static ValidationIssue Warning(string code, string nodeId, string message)
        => new(Severity.Warning, code, nodeId, message);
}
