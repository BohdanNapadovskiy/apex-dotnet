using System.Reflection;
using System.Text;
using System.Text.Json;
using Apex.PdfEdit.Core.Io;

namespace Apex.PdfEdit.Core.Validator;

public static class SchemaLoader
{
    public const string DefaultResource = "Apex.PdfEdit.Core.Resources.schema.tag-validation-rev1.2.json";

    public static SchemaRules LoadDefault()
    {
        var assembly = typeof(SchemaLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(DefaultResource)
                           ?? throw new FileNotFoundException(
                               $"Bundled schema resource not found: {DefaultResource}. " +
                               $"Available: [{string.Join(", ", assembly.GetManifestResourceNames())}]");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    public static SchemaRules Load(string path)
    {
        var raw = File.ReadAllText(path, Encoding.UTF8);
        return Parse(raw);
    }

    private static SchemaRules Parse(string raw)
    {
        // Rev 1.1 was shipped with a stray leading `"` in some copies. Strip if present.
        var stripped = raw;
        if (stripped.Length > 0 && stripped[0] == '\uFEFF')
        {
            stripped = stripped[1..];
        }
        var trimmed = stripped.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            int idx = stripped.IndexOf('"');
            stripped = stripped[..idx] + stripped[(idx + 1)..];
        }

        var byTag = JsonSerializer.Deserialize<Dictionary<string, TagRule>>(stripped, JsonOptions.Default)
                    ?? throw new InvalidDataException("Schema JSON parsed as null.");
        return new SchemaRules(byTag);
    }
}
