using Apex.PdfEdit.Core.Edit;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Demo-aid renderer that stamps a colour-coded outline around each MCID an edit
/// touched, on top of the (already-emitted) edited PDF. Off by default — enabled
/// via <c>--diff-overlay</c> on the <c>edit</c> CLI subcommand.
///
/// Colour code:
/// <list type="bullet">
///   <item>Green — <c>setText</c> target MCID</item>
///   <item>Blue — <c>addParagraph</c> new MCID</item>
///   <item>Cyan — <c>addListItem</c> Lbl and LBody (both columns outlined)</item>
///   <item>Red — <c>deleteNode</c> pre-edit bbox</item>
/// </list>
///
/// Not visualised: <see cref="MoveOverlay"/> shifts (would need post-shift bbox not carried on the overlay).
/// </summary>
public static class DiffOverlayRenderer
{
    private static readonly DeviceRgb Green = new(0, 160, 0);
    private static readonly DeviceRgb Blue = new(0, 90, 220);
    private static readonly DeviceRgb Cyan = new(0, 170, 200);
    private static readonly DeviceRgb Red = new(220, 30, 30);
    private const float LineWidthPt = 1.0f;

    /// <summary>
    /// Copy <paramref name="editedPdf"/> to <paramref name="diffPdf"/> with colour-coded
    /// rectangles stamped on top of every edited MCID. Zero-area entries are skipped.
    /// </summary>
    public static void Apply(string editedPdf, string diffPdf, EditPlan? plan, ILogger? logger = null)
    {
        if (plan is null) return;
        var log = logger ?? NullLogger.Instance;
        var byPage = GroupByPage(plan);
        if (byPage.Count == 0)
        {
            log.LogInformation("--diff-overlay: no overlays to visualise; writing unmodified copy to {Diff}", diffPdf);
        }
        using var reader = new PdfReader(editedPdf);
        using var writer = new PdfWriter(diffPdf);
        using var doc = new PdfDocument(reader, writer);

        foreach (var (pageNum, rects) in byPage)
        {
            if (pageNum < 1 || pageNum > doc.GetNumberOfPages()) continue;
            var page = doc.GetPage(pageNum);
            var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), doc);
            canvas.SaveState().SetLineWidth(LineWidthPt);
            foreach (var r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                canvas.SetStrokeColor(r.Color)
                    .Rectangle(r.X, r.Y, r.Width, r.Height)
                    .Stroke();
            }
            canvas.RestoreState();
        }
        log.LogInformation("--diff-overlay: wrote {Diff}", diffPdf);
    }

    /// <summary>Compute <c>&lt;name&gt;-diff.pdf</c> sibling path for a given edited PDF path.</summary>
    public static string DiffPathFor(string editedPdf)
    {
        var name = Path.GetFileName(editedPdf);
        int dot = name.LastIndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var ext = dot < 0 ? string.Empty : name[dot..];
        var parent = Path.GetDirectoryName(editedPdf);
        var diffName = stem + "-diff" + ext;
        return string.IsNullOrEmpty(parent) ? diffName : Path.Combine(parent, diffName);
    }

    private static Dictionary<int, List<Rect>> GroupByPage(EditPlan plan)
    {
        var byPage = new Dictionary<int, List<Rect>>();
        foreach (var o in plan.SetTextOverlays)
        {
            GetOrAdd(byPage, o.Page).Add(new Rect(o.X, o.Y, o.Width, o.Height, Green));
        }
        foreach (var o in plan.AddParagraphOverlays)
        {
            GetOrAdd(byPage, o.Page).Add(new Rect(o.X, o.Y, o.Width, o.Height, Blue));
        }
        foreach (var o in plan.AddListItemOverlays)
        {
            var pageRects = GetOrAdd(byPage, o.Page);
            pageRects.Add(new Rect(o.LblX, o.LblY, o.LblWidth, o.LblHeight, Cyan));
            pageRects.Add(new Rect(o.BodyX, o.BodyY, o.BodyWidth, o.BodyHeight, Cyan));
        }
        foreach (var o in plan.DeleteOverlays)
        {
            GetOrAdd(byPage, o.Page).Add(new Rect(o.X, o.Y, o.Width, o.Height, Red));
        }
        return byPage;
    }

    private static List<Rect> GetOrAdd(Dictionary<int, List<Rect>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<Rect>();
            map[key] = list;
        }
        return list;
    }

    private sealed record Rect(double X, double Y, double Width, double Height, DeviceRgb Color);
}
