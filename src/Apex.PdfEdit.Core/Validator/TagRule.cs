namespace Apex.PdfEdit.Core.Validator;

public sealed class TagRule
{
    public List<string> AllowedParents { get; set; } = new();
    public List<string> AllowedChildren { get; set; } = new();
    public List<string> RequiredChildren { get; set; } = new();
    public List<string> RequiredChecks { get; set; } = new();
}
