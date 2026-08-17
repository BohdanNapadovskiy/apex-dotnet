using System.Text.Json;
using Apex.PdfEdit.Core.Model;

namespace Apex.PdfEdit.Core.Io;

public static class GeometryJsonLoader
{
    public static GeometryJson Load(string path)
    {
        using var stream = File.OpenRead(path);
        var geom = JsonSerializer.Deserialize<GeometryJson>(stream, JsonOptions.Default)
                   ?? throw new InvalidDataException($"geometry.json parsed as null: {path}");
        return geom;
    }
}
