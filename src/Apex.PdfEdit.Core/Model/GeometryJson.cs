using System.Globalization;

namespace Apex.PdfEdit.Core.Model;

/// <summary>
/// Mirror of the customer's <c>*-geometry.json</c> file. Three parallel views of
/// the same page glyph data:
/// <list type="bullet">
///   <item><see cref="PageSearchText"/> — text runs (roughly one per MCID group) with per-character bboxes.</item>
///   <item><see cref="PageWordBounds"/> — flat glyph list per page (despite the name, entries are single glyphs).</item>
///   <item><see cref="PageMcidWords"/> — glyph list keyed by page then by MCID. Primary source for the writer.</item>
/// </list>
/// All page keys are strings (JSON object keys). MCID keys in <see cref="PageMcidWords"/> are also strings.
/// </summary>
public sealed class GeometryJson
{
    public Dictionary<string, List<TextRun>>? PageSearchText { get; set; }
    public Dictionary<string, List<Glyph>>? PageWordBounds { get; set; }
    public Dictionary<string, Dictionary<string, List<Glyph>>>? PageMcidWords { get; set; }

    /// <summary>Glyphs for a (page, mcid) pair, or empty list if missing.</summary>
    public IReadOnlyList<Glyph> GlyphsFor(int page, int mcid)
    {
        if (PageMcidWords is null) return Array.Empty<Glyph>();
        if (!PageMcidWords.TryGetValue(page.ToString(CultureInfo.InvariantCulture), out var byMcid) || byMcid is null)
        {
            return Array.Empty<Glyph>();
        }
        if (!byMcid.TryGetValue(mcid.ToString(CultureInfo.InvariantCulture), out var glyphs) || glyphs is null)
        {
            return Array.Empty<Glyph>();
        }
        return glyphs;
    }
}
