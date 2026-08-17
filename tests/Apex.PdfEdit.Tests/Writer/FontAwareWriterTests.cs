using System.Text.RegularExpressions;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;
using Path = System.IO.Path;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class FontAwareWriterTests
{
    private const string Sample = "form-40x-2016-Remediated";
    private const string DocPath = Sample + "/" + Sample + "-document.json";
    private const string GeomPath = Sample + "/" + Sample + "-geometry.json";
    private const string PdfPath = Sample + "/" + Sample + ".pdf";

    [FactIfSample(PdfPath)]
    public void FontAwareWriterPreservesTextAndTaggingForForm40x()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            new FontAwareWriter(PageSize.LETTER, resolver).Write(doc, geom, outBuf);
        }

        var bytes = outBuf.ToArray();
        bytes.Should().NotBeEmpty();

        var debug = Path.Combine(TestOutputs.ForSample(Sample), Sample + "_font-aware.pdf");
        File.WriteAllBytes(debug, bytes);

        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().Be(4);
        pdf.IsTagged().Should().BeTrue();

        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        var normalised = Regex.Replace(page1, @"\s+", "");
        normalised.Should().Contain("Dakota").And.Contain("Commissioner").And.Contain("2016");
    }

    [FactIfSample(PdfPath)]
    public void FontAwareOutputIsSubstantiallyLargerThanIdentityPlaceholder()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(GeomPath));

        var fontAware = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(TestSamples.Resolve(PdfPath)))
        {
            new FontAwareWriter(PageSize.LETTER, resolver).Write(doc, geom, fontAware);
        }
        fontAware.Length.Should().BeGreaterThan(20_000);
    }
}
