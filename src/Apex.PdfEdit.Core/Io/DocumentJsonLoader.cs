using System.Text.Json;
using Apex.PdfEdit.Core.Model;

namespace Apex.PdfEdit.Core.Io;

public static class DocumentJsonLoader
{
    public static DocumentJson Load(string path)
    {
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<DocumentJson>(stream, JsonOptions.Default)
                  ?? throw new InvalidDataException($"document.json parsed as null: {path}");
        return doc;
    }
}
