using System.Globalization;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Shared helpers for mapping <see cref="FontStyle"/> to iText's standard-14 fonts and
/// for parsing <c>#RRGGBB</c> colour strings. Used by writers when the source PDF's
/// own font can't be reused (or hasn't been resolved yet).
/// </summary>
public static class StandardFontMapper
{
    public static string MapToStandardFont(FontStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        string family = style.Family?.ToLowerInvariant() ?? string.Empty;
        bool bold = string.Equals(style.Weight, "bold", StringComparison.OrdinalIgnoreCase)
                    || family.Contains("bold", StringComparison.Ordinal);
        bool italic = family.Contains("italic", StringComparison.Ordinal)
                      || family.Contains("oblique", StringComparison.Ordinal);

        if (family.Contains("times", StringComparison.Ordinal)
            || family.Contains("serif", StringComparison.Ordinal)
            || family.Contains("roman", StringComparison.Ordinal)
            || family.Contains("cambria", StringComparison.Ordinal))
        {
            if (bold && italic) return StandardFonts.TIMES_BOLDITALIC;
            if (bold) return StandardFonts.TIMES_BOLD;
            if (italic) return StandardFonts.TIMES_ITALIC;
            return StandardFonts.TIMES_ROMAN;
        }
        if (family.Contains("courier", StringComparison.Ordinal)
            || family.Contains("mono", StringComparison.Ordinal)
            || family.Contains("consolas", StringComparison.Ordinal))
        {
            if (bold && italic) return StandardFonts.COURIER_BOLDOBLIQUE;
            if (bold) return StandardFonts.COURIER_BOLD;
            if (italic) return StandardFonts.COURIER_OBLIQUE;
            return StandardFonts.COURIER;
        }
        if (bold && italic) return StandardFonts.HELVETICA_BOLDOBLIQUE;
        if (bold) return StandardFonts.HELVETICA_BOLD;
        if (italic) return StandardFonts.HELVETICA_OBLIQUE;
        return StandardFonts.HELVETICA;
    }

    public static Color ParseHex(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return new DeviceRgb(0, 0, 0);
        }
        try
        {
            int r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new DeviceRgb(r, g, b);
        }
        catch (FormatException)
        {
            return new DeviceRgb(0, 0, 0);
        }
    }
}
