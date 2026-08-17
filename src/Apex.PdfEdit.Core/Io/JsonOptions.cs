using System.Text.Json;

namespace Apex.PdfEdit.Core.Io;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for all model / edits / schema loaders.
/// Mirrors Jackson's default field-name matching used in the Java side:
/// camelCase property names, tolerant of unknown fields, case-insensitive on read.
/// </summary>
public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
