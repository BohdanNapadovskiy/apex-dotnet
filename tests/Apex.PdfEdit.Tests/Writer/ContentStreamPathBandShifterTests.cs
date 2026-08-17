using System.Text;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Builds a synthetic 1-page PDF with four rectangles: one entirely below the band
/// (should translate), one straddling (should grow bottom), one entirely above
/// (unchanged), one inside a tagged /P BDC (should be left untouched — mover owns
/// that path).
/// </summary>
public sealed class ContentStreamPathBandShifterTests
{
    [Fact]
    public void ShiftsRectBelowBand_GrowsStraddling_LeavesAbove()
    {
        var before = BuildPdfWithRects();
        var shifted = ApplyShifter(before, new PathBandOverlay(1, 300.0, -14.0));
        var stream = ReadPage1ContentStream(shifted);

        // Below-band: y shifted 100 → 86 (3-decimal fmt).
        stream.Should().Contain("10.000 86.000 500.000 50.000 re");
        // Straddling: y shifted, h grown.
        stream.Should().Contain("10.000 186.000 500.000 214.000 re");
        // Above-band: unchanged (pass-through preserves iText's original format).
        stream.Should().Contain("10 500 500 50 re");
        // Tagged rect: unchanged — mover owns tagged content, not this pass.
        stream.Should().Contain("10 250 500 40 re");
    }

    [Fact]
    public void NoBandsLeavesStreamUntouched()
    {
        var before = BuildPdfWithRects();
        var stream = ReadPage1ContentStream(ApplyShifter(before /* no bands */));

        stream.Should().Contain("10 100 500 50 re");
        stream.Should().Contain("10 200 500 200 re");
        stream.Should().Contain("10 500 500 50 re");
        stream.Should().Contain("10 250 500 40 re");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Build a 1-page PDF containing four rectangles — three untagged (below / straddling /
    /// above the band) and one inside a tagged /P BDC.
    /// </summary>
    private static byte[] BuildPdfWithRects()
    {
        var out_ = new MemoryStream();
        using (var writer = new PdfWriter(out_))
        using (var doc = new PdfDocument(writer))
        {
            var page = doc.AddNewPage();
            var c = new PdfCanvas(page);

            c.SaveState().Rectangle(10, 100, 500, 50).Stroke().RestoreState();
            c.SaveState().Rectangle(10, 200, 500, 200).Stroke().RestoreState();
            c.SaveState().Rectangle(10, 500, 500, 50).Stroke().RestoreState();

            var props = new PdfDictionary();
            props.Put(PdfName.MCID, new PdfNumber(0));
            c.BeginMarkedContent(PdfName.P, props);
            c.Rectangle(10, 250, 500, 40).Stroke();
            c.EndMarkedContent();
        }
        return out_.ToArray();
    }

    /// <summary>
    /// Read <paramref name="pdfBytes"/>, apply the shifter with the given bands, return the
    /// rewritten PDF bytes.
    /// </summary>
    private static byte[] ApplyShifter(byte[] pdfBytes, params PathBandOverlay[] bands)
    {
        var out_ = new MemoryStream();
        using (var reader = new PdfReader(new MemoryStream(pdfBytes)))
        using (var writer = new PdfWriter(out_))
        using (var doc = new PdfDocument(reader, writer))
        {
            ContentStreamPathBandShifter.Apply(doc.GetPage(1), bands);
        }
        return out_.ToArray();
    }

    private static string ReadPage1ContentStream(byte[] pdfBytes)
    {
        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var doc = new PdfDocument(reader);
        var contents = doc.GetPage(1).GetContentBytes();
        return Encoding.Latin1.GetString(contents);
    }
}
