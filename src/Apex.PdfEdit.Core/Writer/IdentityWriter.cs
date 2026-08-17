using System.Text.Json;
using Apex.PdfEdit.Core.Model;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Tagutils;
using iText.Layout;
using iText.Layout.Element;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Prototype identity writer. Reads a <see cref="DocumentJson"/> and emits a tagged PDF
/// that stamps each content-bearing node at its recorded bbox.
///
/// Rev 1 §8.3 goal: confirm iText 9 accepts the customer's geometry as-is.
/// Not a real writer — fixed Helvetica, no real font/size/color/weight recovery,
/// no reflow, no MCID preservation. Every text run gets a fresh MCID from iText.
/// </summary>
public sealed class IdentityWriter
{
    private readonly PageSize _pageSize;

    public IdentityWriter(PageSize pageSize)
    {
        _pageSize = pageSize;
    }

    public void Write(DocumentJson doc, Stream output)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        int pageCount = ResolvePageCount(doc);

        using var writer = new PdfWriter(output);
        using var pdf = new PdfDocument(writer);
        pdf.SetTagged();

        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
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
            var pdfCanvas = new PdfCanvas(pdf.GetPage(p));
            tag.SetPageForTagging(pdf.GetPage(p));

            foreach (var n in nodes)
            {
                DrawNode(n, pdfCanvas, tag, font);
            }
        }
    }

    private static int ResolvePageCount(DocumentJson doc)
    {
        if (doc.DocumentProperties is JsonElement props
            && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("numberOfPages", out var np)
            && np.ValueKind == JsonValueKind.Number
            && np.TryGetInt32(out var pages))
        {
            return Math.Max(1, pages);
        }
        return MaxPage(doc.Tree);
    }

    private static int MaxPage(List<TreeNode> tree)
    {
        int max = 1;
        foreach (var n in tree)
        {
            if (n.Page > max) max = n.Page;
        }
        return max;
    }

    private static void DrawNode(TreeNode n, PdfCanvas pdfCanvas, TagTreePointer tag, PdfFont font)
    {
        var role = MapRole(n.Text);
        tag.AddTag(role);
        try
        {
            var box = new Rectangle(
                (float)n.X,
                (float)n.Y,
                (float)Math.Max(n.Width, 1.0),
                (float)Math.Max(n.Height, 1.0));
            using var layoutCanvas = new Canvas(pdfCanvas, box);
            layoutCanvas.EnableAutoTagging(pdfCanvas.GetDocument().GetFirstPage());
            float fontSize = FontSizeFor(n);
            var paragraph = new Paragraph(n.Content)
                .SetFont(font)
                .SetFontSize(fontSize)
                .SetMargin(0)
                .SetPadding(0)
                .SetMultipliedLeading(1.0f);
            layoutCanvas.Add(paragraph);
        }
        finally
        {
            tag.MoveToParent();
        }
    }

    private static float FontSizeFor(TreeNode n)
    {
        // Approximate: iText leading ~= 1.2 * fontSize; typical bbox height ~= fontSize * 1.15.
        // Clamp to a sane range so degenerate bboxes don't produce microscopic or huge text.
        double h = n.Height;
        double approx = h > 0 ? h * 0.85 : 10.0;
        if (approx < 5.0) approx = 5.0;
        if (approx > 72.0) approx = 72.0;
        return (float)approx;
    }

    /// <summary>Map the JSON <c>text</c> (tag role) to iText's StandardRoles; fall back to P for unknown.</summary>
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
}
