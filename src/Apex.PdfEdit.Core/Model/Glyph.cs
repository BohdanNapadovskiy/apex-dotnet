using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apex.PdfEdit.Core.Model;

/// <summary>
/// A single glyph with its bounding box on the page.
///
/// Field naming in geometry.json is inconsistent: <c>pageSearchText[..].Chars[].C</c>
/// carries the character in <c>C</c>, while <c>pageWordBounds[]</c> and
/// <c>pageMcidWords[..][..]</c> carry it in <c>Text</c>. Both are accepted here
/// via <see cref="GlyphJsonConverter"/>.
/// </summary>
[JsonConverter(typeof(GlyphJsonConverter))]
public sealed class Glyph
{
    public string? Text { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

internal sealed class GlyphJsonConverter : JsonConverter<Glyph>
{
    public override Glyph Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject for Glyph, got {reader.TokenType}");
        }

        var glyph = new Glyph();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return glyph;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName in Glyph, got {reader.TokenType}");
            }
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "C":
                case "Text":
                    glyph.Text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    break;
                case "X":
                    glyph.X = ReadDouble(ref reader);
                    break;
                case "Y":
                    glyph.Y = ReadDouble(ref reader);
                    break;
                case "Width":
                    glyph.Width = ReadDouble(ref reader);
                    break;
                case "Height":
                    glyph.Height = ReadDouble(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        throw new JsonException("Unexpected end of JSON while reading Glyph");
    }

    public override void Write(Utf8JsonWriter writer, Glyph value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Text is not null) writer.WriteString("Text", value.Text);
        writer.WriteNumber("X", value.X);
        writer.WriteNumber("Y", value.Y);
        writer.WriteNumber("Width", value.Width);
        writer.WriteNumber("Height", value.Height);
        writer.WriteEndObject();
    }

    private static double ReadDouble(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetDouble(),
        JsonTokenType.Null => 0.0,
        _ => throw new JsonException($"Expected number for Glyph coordinate, got {reader.TokenType}")
    };
}
