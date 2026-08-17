using System.Text.Json;
using Apex.PdfEdit.Core.Model;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Tagutils;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Emits a tagged PDF from a <see cref="DocumentJson"/> using:
/// <list type="bullet">
///   <item><see cref="GeometryJson.GlyphsFor"/> for per-glyph placement,</item>
///   <item><see cref="SourcePdfFontResolver"/> for font family / size / weight / colour per (page, mcid).</item>
/// </list>
///
/// Per §5.1 the JSON pair is the sole source of structure and content; the source PDF
/// is used only through the resolver as a font-and-style reference (§5.3 option (b)).
///
/// Limitations:
/// <list type="bullet">
///   <item>Custom / embedded fonts are mapped to a standard-14 approximation by family-name
///       substring — Times / Courier / Helvetica plus bold + italic variants.</item>
///   <item>Images, rules, and other non-text page content are not reproduced.</item>
///   <item>Nodes with mcid &lt; 0, artifacts, or empty content are skipped.</item>
/// </list>
/// </summary>
public sealed class FontAwareWriter
{
    private static readonly FontStyle FallbackStyle =
        new(null, 10.0f, "regular", "#000000");

    private readonly PageSize _pageSize;
    private readonly SourcePdfFontResolver _fontResolver;
    private readonly Dictionary<string, PdfFont> _fontCache = new();

    public FontAwareWriter(PageSize pageSize, SourcePdfFontResolver fontResolver)
    {
        _pageSize = pageSize;
        _fontResolver = fontResolver;
    }

    public void Write(DocumentJson doc, GeometryJson? geometry, Stream output)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        int pageCount = PageCount(doc);

        using var writer = new PdfWriter(output);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();
        for (int i = 0; i < pageCount; i++)
        {
            pdf.AddNewPage(_pageSize);
        }

        var byPage = doc.Tree
            .Where(n => n.Mcid >= 0)
            .Where(n => !n.IsArtifact)
            .Where(n => !string.IsNullOrEmpty(n.Content))
            .Where(n => n.Page >= 1 && n.Page <= pageCount)
            .GroupBy(n => n.Page)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Order).ToList());

        var tag = new TagTreePointer(pdf);

        for (int p = 1; p <= pageCount; p++)
        {
            if (!byPage.TryGetValue(p, out var nodes)) continue;
            tag.SetPageForTagging(pdf.GetPage(p));
            var canvas = new PdfCanvas(pdf.GetPage(p));
            foreach (var node in nodes)
            {
                DrawNode(node, canvas, tag, geometry);
            }
        }
    }

    private void DrawNode(TreeNode node, PdfCanvas canvas, TagTreePointer tag, GeometryJson? geometry)
    {
        tag.AddTag(MapRole(node.Text));
        try
        {
            var style = _fontResolver.Resolve(node.Page, node.Mcid) ?? FallbackStyle;
            var glyphs = geometry is not null
                ? geometry.GlyphsFor(node.Page, node.Mcid)
                : Array.Empty<Glyph>();

            var font = FontFor(style);
            var color = StandardFontMapper.ParseHex(style.ColorHex);
            float fontSize = EffectiveSize(style, node);

            var tagRef = tag.GetTagReference();
            canvas.OpenTag(tagRef);
            canvas.SaveState();
            canvas.SetFillColor(color);
            canvas.BeginText();
            canvas.SetFontAndSize(font, fontSize);

            if (glyphs.Count > 0)
            {
                foreach (var g in glyphs)
                {
                    if (g.Text is null) continue;
                    canvas.SetTextMatrix((float)g.X, (float)g.Y);
                    canvas.ShowText(g.Text);
                }
            }
            else
            {
                canvas.SetTextMatrix((float)node.X, (float)node.Y);
                canvas.ShowText(node.Content);
            }

            canvas.EndText();
            canvas.RestoreState();
            canvas.CloseTag();
        }
        finally
        {
            tag.MoveToParent();
        }
    }

    private static float EffectiveSize(FontStyle style, TreeNode node)
    {
        if (style.Size >= 4.0f) return style.Size;
        // Resolver produced an unusable size — approximate from bbox.
        double approx = node.Height > 0 ? node.Height * 0.85 : 10.0;
        if (approx < 5.0) approx = 5.0;
        if (approx > 72.0) approx = 72.0;
        return (float)approx;
    }

    private PdfFont FontFor(FontStyle style)
    {
        var cacheKey = (style.Family ?? "-") + "/" + (style.Weight ?? "regular");
        if (_fontCache.TryGetValue(cacheKey, out var cached)) return cached;
        var font = PdfFontFactory.CreateFont(StandardFontMapper.MapToStandardFont(style));
        _fontCache[cacheKey] = font;
        return font;
    }

    private static string MapRole(string? jsonRole) => jsonRole switch
    {
        null => StandardRoles.P,
        "Document" => StandardRoles.DOCUMENT,
        "Part" => StandardRoles.PART,
        "Sect" => StandardRoles.SECT,
        "Div" => StandardRoles.DIV,
        "P" => StandardRoles.P,
        "H1" => StandardRoles.H1,
        "H2" => StandardRoles.H2,
        "H3" => StandardRoles.H3,
        "H4" => StandardRoles.H4,
        "H5" => StandardRoles.H5,
        "H6" => StandardRoles.H6,
        "L" => StandardRoles.L,
        "LI" => StandardRoles.LI,
        "Lbl" => StandardRoles.LBL,
        "LBody" => StandardRoles.LBODY,
        "Table" => StandardRoles.TABLE,
        "TR" => StandardRoles.TR,
        "TH" => StandardRoles.TH,
        "TD" => StandardRoles.TD,
        "Figure" => StandardRoles.FIGURE,
        "Link" => StandardRoles.LINK,
        "Span" => StandardRoles.SPAN,
        "Reference" => StandardRoles.REFERENCE,
        "Note" => StandardRoles.NOTE,
        "Form" => StandardRoles.FORM,
        _ => StandardRoles.P
    };

    private static int PageCount(DocumentJson doc)
    {
        if (doc.DocumentProperties is JsonElement props
            && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("numberOfPages", out var np)
            && np.ValueKind == JsonValueKind.Number
            && np.TryGetInt32(out var pages))
        {
            return Math.Max(1, pages);
        }
        int max = 1;
        foreach (var n in doc.Tree)
        {
            if (n.Page > max) max = n.Page;
        }
        return max;
    }
}
