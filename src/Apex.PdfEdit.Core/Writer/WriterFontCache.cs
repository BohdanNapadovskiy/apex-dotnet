using iText.Kernel.Font;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Per-write-pass font cache. Ensures at most one <see cref="PdfFont"/> per (family, weight)
/// is loaded from disk during a single <c>SourceBasedWriter.Write</c> call, even when
/// multiple overlays on different pages resolve to the same universal fallback.
///
/// Without this cache, each ContentStreamMcidReplacer / AddParagraphStamper call
/// independently invokes <see cref="SystemFontLocator.Load"/>, which via
/// <see cref="PdfFontFactory.CreateFont(string)"/> emits a fresh embedded subset. Two
/// identical-family subsets in one PDF trip PAC 2024's "3 Components required, but found 1"
/// fatal.
///
/// Scope is exactly one <see cref="iText.Kernel.Pdf.PdfDocument"/> instance:
/// <see cref="PdfFont"/> objects are bound to a specific document, so the cache is
/// created fresh at the top of each write and discarded when the write finishes.
/// </summary>
internal sealed class WriterFontCache
{
    private readonly Dictionary<string, PdfFont> _cache = new();

    /// <summary>Load the given family+weight from the OS font directory, caching the result.</summary>
    internal PdfFont? Load(string? family, string? weight)
    {
        var key = Key("load", family, weight);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var loaded = SystemFontLocator.Load(family, weight);
        if (loaded is not null) _cache[key] = loaded;
        return loaded;
    }

    /// <summary>Load the universal Arial/Times/Courier fallback for the given style, caching.</summary>
    internal PdfFont? LoadUniversalFallback(FontStyle? style)
    {
        var key = Key("universal",
            style?.Family ?? string.Empty,
            style?.Weight ?? string.Empty);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var loaded = SystemFontLocator.LoadUniversalFallback(style);
        if (loaded is not null) _cache[key] = loaded;
        return loaded;
    }

    private static string Key(string kind, string? family, string? weight)
    {
        var f = (family ?? string.Empty).ToLowerInvariant().Trim();
        var w = (weight ?? string.Empty).ToLowerInvariant().Trim();
        return $"{kind}|{f}|{w}";
    }
}
