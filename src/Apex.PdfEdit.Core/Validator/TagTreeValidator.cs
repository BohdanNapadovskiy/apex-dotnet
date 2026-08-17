using Apex.PdfEdit.Core.Model;

namespace Apex.PdfEdit.Core.Validator;

/// <summary>
/// Walks the flat <c>tree[]</c> in a <see cref="DocumentJson"/> and reports violations
/// against a <see cref="SchemaRules"/>.
///
/// Checks implemented:
/// <list type="bullet">
///   <item><c>DANGLING_PARENT</c> (ERROR) — a node's <c>parent</c> id does not exist.</item>
///   <item><c>UNKNOWN_TAG</c> (WARNING) — the node's tag role is not defined in the schema.</item>
///   <item><c>PARENT_REJECTS_CHILD</c> (ERROR) — parent's tag rule does not list the child tag in <c>allowedChildren</c>.</item>
///   <item><c>CHILD_REJECTS_PARENT</c> (ERROR) — child's tag rule does not list the parent tag in <c>allowedParents</c>.</item>
/// </list>
/// Not yet implemented: <c>requiredChildren</c>, <c>requiredChecks</c> (e.g. <c>HAS_CONTENT</c>),
/// MCID uniqueness, ParentTree consistency, attribute schemas.
/// </summary>
public sealed class TagTreeValidator
{
    private const string RootParent = "#";

    private readonly SchemaRules _schema;

    public TagTreeValidator(SchemaRules schema)
    {
        _schema = schema;
    }

    public ValidationReport Validate(DocumentJson document)
    {
        var issues = new List<ValidationIssue>();
        var byId = new Dictionary<string, TreeNode>();
        foreach (var n in document.Tree)
        {
            if (n.Id is not null) byId[n.Id] = n;
        }

        foreach (var node in document.Tree)
        {
            var tag = node.Text ?? string.Empty;
            var nodeId = node.Id ?? string.Empty;

            if (!_schema.IsDefined(tag))
            {
                issues.Add(ValidationIssue.Warning(
                    "UNKNOWN_TAG",
                    nodeId,
                    $"Tag `{tag}` is not defined in the schema."));
            }

            if (RootParent.Equals(node.Parent, StringComparison.Ordinal))
            {
                continue;
            }

            if (node.Parent is null || !byId.TryGetValue(node.Parent, out var parent))
            {
                issues.Add(ValidationIssue.Error(
                    "DANGLING_PARENT",
                    nodeId,
                    $"Parent id `{node.Parent}` does not exist."));
                continue;
            }

            var parentTag = parent.Text ?? string.Empty;
            var parentRule = _schema.Rule(parentTag);
            var childRule = _schema.Rule(tag);

            if (parentRule is not null && !parentRule.AllowedChildren.Contains(tag))
            {
                issues.Add(ValidationIssue.Error(
                    "PARENT_REJECTS_CHILD",
                    nodeId,
                    $"Parent tag `{parentTag}` (id={parent.Id}) does not allow child `{tag}`."));
            }

            if (childRule is not null && !childRule.AllowedParents.Contains(parentTag))
            {
                issues.Add(ValidationIssue.Error(
                    "CHILD_REJECTS_PARENT",
                    nodeId,
                    $"Child tag `{tag}` does not allow parent `{parentTag}` (id={parent.Id})."));
            }
        }

        return new ValidationReport(issues);
    }
}
