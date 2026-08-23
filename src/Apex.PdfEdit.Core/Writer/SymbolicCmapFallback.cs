using SkiaSharp;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// TrueType fonts with a symbolic (3,0) cmap — common in ABBYY OCR output — expose their
/// glyphs at U+F000..U+F0FF instead of the natural Unicode positions, so a direct
/// <see cref="SKFont.GetGlyphs(string)"/> lookup returns .notdef for plain ASCII.
/// Java's AWT retries <c>0xF000 | c</c> transparently; SkiaSharp needs it done by hand.
/// </summary>
internal static class SymbolicCmapFallback
{
    internal static ushort GlyphId(SKFont font, int cp)
    {
        Span<ushort> glyphs = stackalloc ushort[1];
        ReadOnlySpan<int> direct = stackalloc int[] { cp };
        font.GetGlyphs(direct, glyphs);
        if (glyphs[0] != 0) return glyphs[0];
        if (cp is < 0x20 or > 0xFF) return 0;
        ReadOnlySpan<int> symbolic = stackalloc int[] { 0xF000 | cp };
        font.GetGlyphs(symbolic, glyphs);
        return glyphs[0];
    }

    /// <summary>
    /// Rewrite <paramref name="line"/> so every char that only resolves through the
    /// F0xx symbolic range is replaced by its PUA counterpart. Chars that resolve
    /// directly (or not at all) pass through unchanged.
    /// </summary>
    internal static string RemapLine(SKFont font, string line)
    {
        Span<ushort> glyph = stackalloc ushort[1];
        Span<int> probe = stackalloc int[1];
        char[]? remapped = null;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsSurrogate(c) || c is < (char)0x20 or > (char)0xFF) continue;
            probe[0] = c;
            font.GetGlyphs(probe, glyph);
            if (glyph[0] != 0) continue;
            probe[0] = 0xF000 | c;
            font.GetGlyphs(probe, glyph);
            if (glyph[0] == 0) continue;
            remapped ??= line.ToCharArray();
            remapped[i] = (char)(0xF000 | c);
        }
        return remapped is null ? line : new string(remapped);
    }
}
