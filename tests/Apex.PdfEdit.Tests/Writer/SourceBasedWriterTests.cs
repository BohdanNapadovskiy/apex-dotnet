using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class SourceBasedWriterTests
{
    private const string Form40xPdf = "form-40x-2016-Remediated/form-40x-2016-Remediated.pdf";

    [FactIfSample(Form40xPdf)]
    public void IdentityCopyPreservesPageCountAndTagging()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var outBuf = new MemoryStream();
        new SourceBasedWriter(srcPath).Write(outBuf);

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().Be(4);
        pdf.IsTagged().Should().BeTrue();
    }

    [FactIfSample(Form40xPdf)]
    public void IdentityCopyPreservesExactText()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var outBuf = new MemoryStream();
        new SourceBasedWriter(srcPath).Write(outBuf);

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().Contain("North Dakota Office of State Tax Commissioner");
        page1.Should().Contain("Form 40X Amended Corporation Income Tax Return");
        page1.Should().Contain("Calendar Year or Fiscal Year beginning");
    }

    [FactIfSample(Form40xPdf)]
    public void IdentityCopyPreservesAcroFormFields()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var outBuf = new MemoryStream();
        new SourceBasedWriter(srcPath).Write(outBuf);

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var acro = pdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
        acro.Should().NotBeNull("catalog /AcroForm");
        var fields = acro!.GetAsArray(PdfName.Fields);
        fields.Should().NotBeNull("AcroForm /Fields");
        fields!.Size().Should().BeGreaterThanOrEqualTo(80);
    }

    [FactIfSample(Form40xPdf)]
    public void IdentityCopyPreservesPdfUaCatalogEntries()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var outBuf = new MemoryStream();
        new SourceBasedWriter(srcPath).Write(outBuf);

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var cat = pdf.GetCatalog().GetPdfObject();

        cat.Get(PdfName.Lang).Should().NotBeNull("catalog /Lang");
        cat.Get(PdfName.Metadata).Should().NotBeNull("catalog /Metadata (XMP)");

        var vp = cat.GetAsDictionary(PdfName.ViewerPreferences);
        vp.Should().NotBeNull("catalog /ViewerPreferences");

        var markInfo = cat.GetAsDictionary(PdfName.MarkInfo);
        markInfo.Should().NotBeNull("catalog /MarkInfo");
        markInfo!.GetAsBool(PdfName.Marked).Should().BeTrue("/MarkInfo.Marked");

        pdf.GetDocumentInfo().GetTitle().Should().NotBeNullOrWhiteSpace("trailer /Info.Title");
    }

    [FactIfSample(Form40xPdf)]
    public void OutputCarriesEmbeddedContent()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        var outBuf = new MemoryStream();
        new SourceBasedWriter(srcPath).Write(outBuf);
        outBuf.Length.Should().BeGreaterThan(100_000L);
    }
}
