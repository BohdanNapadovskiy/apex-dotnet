using System.Text.Json;
using Apex.PdfEdit.Core.Io;

namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Loads an <see cref="EditsJson"/> from disk, accepting two on-wire shapes:
///
/// <list type="bullet">
///   <item><b>Engine format</b> — <c>{ "schemaVersion", "baseDocument", "operations": [{ "id", "type", "target", ... }] }</c>.
///       Deserialized directly onto <see cref="EditsJson"/>.</item>
///   <item><b>Customer format</b> — <c>{ "schemaVersion", "edits": [{ "targetNodeId", "updatedText", "updatedAt" }] }</c>.
///       Each entry is synthesized into a <see cref="SetTextOp"/> with id
///       <c>edit-{index}</c>. Silent-pass-through on <c>ops=0</c> is what the customer
///       hit against the Ram UAT sample (2026-09-07); this loader raises a clear
///       error instead when neither shape matches.</item>
/// </list>
/// </summary>
public static class EditsJsonLoader
{
    public static EditsJson Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"edits.json root must be a JSON object: {path}");
        }

        // Engine format wins when both are present (belt-and-suspenders — customers
        // don't ship the hybrid, but silent precedence is safer than throwing).
        if (root.TryGetProperty("operations", out _))
        {
            return JsonSerializer.Deserialize<EditsJson>(root.GetRawText(), JsonOptions.Default)
                ?? throw new InvalidDataException($"edits.json parsed as null: {path}");
        }

        if (root.TryGetProperty("edits", out var editsElem)
            && editsElem.ValueKind == JsonValueKind.Array)
        {
            return TranslateCustomerShape(root, editsElem, path);
        }

        throw new InvalidDataException(
            "edits.json has neither 'operations' (engine format) nor 'edits' (customer format): " + path);
    }

    /// <summary>
    /// Convert the customer's high-level <c>{ targetNodeId, updatedText }</c> entries
    /// into engine-format <see cref="SetTextOp"/>s. Preserves top-level metadata
    /// (schemaVersion, baseDocument) so downstream logs stay informative.
    /// </summary>
    private static EditsJson TranslateCustomerShape(JsonElement root, JsonElement editsElem, string path)
    {
        var result = new EditsJson
        {
            SchemaVersion = ReadOptionalString(root, "schemaVersion"),
            BaseDocument = ReadOptionalString(root, "baseDocument"),
        };
        int i = 0;
        foreach (var edit in editsElem.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"edits.json edits[{i}] must be an object in {path}");
            }
            var target = ReadOptionalString(edit, "targetNodeId");
            if (string.IsNullOrEmpty(target))
            {
                throw new InvalidDataException(
                    $"edits.json edits[{i}].targetNodeId missing or empty in {path}");
            }
            if (!edit.TryGetProperty("updatedText", out var updated)
                || updated.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"edits.json edits[{i}].updatedText missing or non-string in {path}");
            }
            result.Operations.Add(SetTextOp.Of("edit-" + i, target!, updated.GetString() ?? string.Empty));
            i++;
        }
        return result;
    }

    private static string? ReadOptionalString(JsonElement obj, string prop)
    {
        return obj.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
    }
}
