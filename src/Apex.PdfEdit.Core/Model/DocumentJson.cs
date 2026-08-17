using System.Text.Json;

namespace Apex.PdfEdit.Core.Model;

public sealed class DocumentJson
{
    public JsonElement? DocumentProperties { get; set; }
    public JsonElement? Summary { get; set; }
    public List<TreeNode> Tree { get; set; } = new();
}
