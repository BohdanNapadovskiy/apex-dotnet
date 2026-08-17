namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Font/style attributes recovered from the source PDF's content stream for a
/// given (page, mcid). All fields are best-effort — <see cref="Family"/> may be null
/// for standard 14 fonts, <see cref="Weight"/> defaults to <c>"regular"</c> when the
/// font program does not expose one.
/// </summary>
/// <param name="Family">Family name as reported by the source font resource, or null when unknown.</param>
/// <param name="Size">Point size at the MCID's Tf state.</param>
/// <param name="Weight"><c>"regular"</c> / <c>"bold"</c> / etc. — inferred from source font metadata.</param>
/// <param name="ColorHex"><c>#RRGGBB</c>.</param>
/// <param name="SourceFontObjNumber">
/// Object number of the source PDF's actual font resource dict for the MCID
/// this style was resolved from, or null if unknown (synthetic tests, pre-refactor callers).
/// Threaded through so PageFontInventory.FindByObjNumber can look up the exact font
/// source used at the MCID — needed when the page carries multiple fonts with
/// the same PostScript BaseFont (Type0 + simple twins) whose subsets contain
/// disjoint glyph sets. Family+weight matching alone can't distinguish; the
/// object number is unique per resource.
/// </param>
/// <param name="LeadingRatio">
/// Ratio of source's text-space TL (leading) to text-space Tf (font size), i.e.
/// the intended line-height multiplier. Dimensionless so it survives the
/// "Tf 1 + Tm-scaled" pattern producers sometimes use.
/// Consumers multiply by their emitted (effective) font size to get lineHeight.
/// A value of <c>0f</c> means "unknown — TL was never set in source" (single-line
/// MCIDs typically), and writers should fall back to the default 1.2 multiplier.
/// </param>
public sealed record FontStyle(
    string? Family,
    float Size,
    string? Weight,
    string? ColorHex,
    int? SourceFontObjNumber,
    float LeadingRatio)
{
    /// <summary>Four-arg overload — pre-Batch-A callers that didn't compute the object number or leading ratio.</summary>
    public FontStyle(string? family, float size, string? weight, string? colorHex)
        : this(family, size, weight, colorHex, null, 0f) { }

    /// <summary>Five-arg overload — callers that computed source font object number but not leading ratio.</summary>
    public FontStyle(string? family, float size, string? weight, string? colorHex, int? sourceFontObjNumber)
        : this(family, size, weight, colorHex, sourceFontObjNumber, 0f) { }
}
