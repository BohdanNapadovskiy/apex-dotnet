using System.Text.Json;
using Apex.PdfEdit.Core.Io;

namespace Apex.PdfEdit.Core.Edit;

public static class EditsJsonLoader
{
    public static EditsJson Load(string path)
    {
        using var stream = File.OpenRead(path);
        var edits = JsonSerializer.Deserialize<EditsJson>(stream, JsonOptions.Default)
                    ?? throw new InvalidDataException($"edits.json parsed as null: {path}");
        return edits;
    }
}
