using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using Xunit;
using Path = System.IO.Path;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class IdentityWriterTests
{
    private const string Sample = "form-40x-2016-Remediated";
    private const string DocPath = Sample + "/" + Sample + "-document.json";

    [FactIfSample(DocPath)]
    public void IdentityWriterProducesTaggedPdfForForm40x()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(DocPath));
        doc.Tree.Should().NotBeEmpty();

        var outBuf = new MemoryStream();
        new IdentityWriter(PageSize.LETTER).Write(doc, outBuf);

        var bytes = outBuf.ToArray();
        bytes.Should().NotBeEmpty();

        var debug = Path.Combine(TestOutputs.ForSample(Sample), Sample + "_identity.pdf");
        File.WriteAllBytes(debug, bytes);

        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().Be(4);
        pdf.IsTagged().Should().BeTrue();
        pdf.GetStructTreeRoot().Should().NotBeNull();
    }
}
