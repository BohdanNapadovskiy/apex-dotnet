using System.Text.Json.Serialization;

namespace Apex.PdfEdit.Core.Edit;

public sealed class EditsJson
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("baseDocument")]
    public string? BaseDocument { get; set; }

    [JsonPropertyName("operations")]
    public List<EditOp> Operations { get; set; } = new();
}
