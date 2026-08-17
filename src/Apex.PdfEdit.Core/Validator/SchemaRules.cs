namespace Apex.PdfEdit.Core.Validator;

public sealed class SchemaRules
{
    private readonly IReadOnlyDictionary<string, TagRule> _byTag;

    public SchemaRules(IDictionary<string, TagRule> byTag)
    {
        _byTag = new Dictionary<string, TagRule>(byTag);
    }

    public IReadOnlySet<string> DefinedTags() => _byTag.Keys.ToHashSet();

    public bool IsDefined(string tag) => _byTag.ContainsKey(tag);

    public TagRule? Rule(string tag) => _byTag.TryGetValue(tag, out var r) ? r : null;
}
