using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Layout;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using Xunit;
using Path = System.IO.Path;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class DiffOverlayRendererTests
{
    [Fact]
    public void DiffPathForInsertsSuffixBeforeExtension()
    {
        var dir = MakeTempDir();
        try
        {
            var outPath = Path.Combine(dir, "Sample-edited.pdf");
            DiffOverlayRenderer.DiffPathFor(outPath)
                .Should().Be(Path.Combine(dir, "Sample-edited-diff.pdf"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DiffPathForHandlesMissingExtension()
    {
        var dir = MakeTempDir();
        try
        {
            var outPath = Path.Combine(dir, "edited");
            DiffOverlayRenderer.DiffPathFor(outPath).Should().Be(Path.Combine(dir, "edited-diff"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DiffPathForRelativePathWorksWithoutParent()
    {
        var diff = DiffOverlayRenderer.DiffPathFor("out.pdf");
        Path.GetFileName(diff).Should().Be("out-diff.pdf");
    }

    [Fact]
    public void ApplyStampsRectanglesAndProducesReadablePdf()
    {
        var dir = MakeTempDir();
        try
        {
            var source = Path.Combine(dir, "in.pdf");
            WriteMinimalOnePagePdf(source);
            var diffOut = Path.Combine(dir, "in-diff.pdf");

            var style = new FontStyle("Arial", 10f, "regular", "#000000");
            var plan = EditPlan.NewBuilder()
                .SetText(new SetTextOverlay(1, 100, 700, 200, 12, "new", style, Alignment.Left, "n1", 3, double.NaN))
                .AddParagraph(new AddParagraphOverlay(1, 100, 650, 300, 40, "P", "new paragraph", style, Alignment.Left, "n2", 1, 5))
                .AddListItem(new AddListItemOverlay(1,
                    60, 600, 20, 14, "1.",
                    90, 600, 300, 14, "new LI body",
                    style, style, Alignment.Left,
                    "n3", "n3-lbl", "n3-body", 1, 7))
                .Delete(new DeleteOverlay(1, 9, "n4", 100, 550, 250, 12))
                .Build();

            DiffOverlayRenderer.Apply(source, diffOut, plan);

            File.Exists(diffOut).Should().BeTrue();
            new FileInfo(diffOut).Length.Should().BeGreaterThan(new FileInfo(source).Length);
            using var r = new PdfReader(diffOut);
            using var d = new PdfDocument(r);
            d.GetNumberOfPages().Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApplySkipsZeroAreaOverlays()
    {
        var dir = MakeTempDir();
        try
        {
            var source = Path.Combine(dir, "in.pdf");
            WriteMinimalOnePagePdf(source);
            var diffOut = Path.Combine(dir, "in-diff.pdf");

            var plan = EditPlan.NewBuilder()
                .Delete(new DeleteOverlay(1, 5, "n"))
                .Build();

            DiffOverlayRenderer.Apply(source, diffOut, plan);

            File.Exists(diffOut).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void WriteMinimalOnePagePdf(string path)
    {
        using var w = new PdfWriter(path);
        using var d = new PdfDocument(w);
        d.AddNewPage(PageSize.A4);
        d.GetPage(1).GetPdfObject().Put(PdfName.Contents,
            new PdfStream(System.Text.Encoding.ASCII.GetBytes("q Q\n")));
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
