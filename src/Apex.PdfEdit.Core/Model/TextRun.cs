using System.Text.Json.Serialization;

namespace Apex.PdfEdit.Core.Model;

public sealed class TextRun
{
    [JsonPropertyName("Text")]
    public string? Text { get; set; }

    [JsonPropertyName("Chars")]
    public List<Glyph>? Chars { get; set; }
}
