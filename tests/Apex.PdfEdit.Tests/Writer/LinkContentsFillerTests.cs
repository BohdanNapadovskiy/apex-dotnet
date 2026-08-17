using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class LinkContentsFillerTests
{
    private static readonly IReadOnlyDictionary<PdfIndirectReference, int> EmptyPageIndex
        = new Dictionary<PdfIndirectReference, int>();

    [Fact]
    public void UriLinkGetsContentsSetToUriString()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.URI);
        action.Put(PdfName.URI, new PdfString("https://www.nd.gov/tax"));
        annot.Put(PdfName.A, action);

        var patched = LinkContentsFiller.PopulateIfMissing(annot, EmptyPageIndex);

        patched.Should().BeTrue();
        annot.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("https://www.nd.gov/tax");
    }

    [Fact]
    public void ExistingContentsIsNotOverwritten()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        annot.Put(PdfName.Contents, new PdfString("Human-authored description"));
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.URI);
        action.Put(PdfName.URI, new PdfString("https://example.com"));
        annot.Put(PdfName.A, action);

        var patched = LinkContentsFiller.PopulateIfMissing(annot, EmptyPageIndex);

        patched.Should().BeFalse();
        annot.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("Human-authored description");
    }

    [Fact]
    public void NonLinkSubtypeSkipped()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Widget);

        var patched = LinkContentsFiller.PopulateIfMissing(annot, EmptyPageIndex);

        patched.Should().BeFalse();
        annot.Get(PdfName.Contents).Should().BeNull();
    }

    [Fact]
    public void GotoRLinkDescribesFilePath()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.GoToR);
        action.Put(PdfName.F, new PdfString("supplement.pdf"));
        annot.Put(PdfName.A, action);

        var patched = LinkContentsFiller.PopulateIfMissing(annot, EmptyPageIndex);

        patched.Should().BeTrue();
        annot.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("Open supplement.pdf");
    }

    [Fact]
    public void GotoWithoutPageIndexFallsBackToInternalLink()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.GoTo);
        var dest = new PdfArray();
        dest.Add(new PdfNumber(0));
        dest.Add(PdfName.Fit);
        action.Put(PdfName.D, dest);
        annot.Put(PdfName.A, action);

        var patched = LinkContentsFiller.PopulateIfMissing(annot, EmptyPageIndex);

        patched.Should().BeTrue();
        annot.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("Internal link");
    }

    [Fact]
    public void GotoWithResolvablePageReferenceGetsGoToPageN()
    {
        var out_ = new MemoryStream();
        using var doc = new PdfDocument(new PdfWriter(out_));
        doc.AddNewPage();
        doc.AddNewPage();
        doc.AddNewPage();

        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        var action = new PdfDictionary();
        action.Put(PdfName.S, PdfName.GoTo);
        var dest = new PdfArray();
        dest.Add(doc.GetPage(2).GetPdfObject());
        dest.Add(PdfName.Fit);
        action.Put(PdfName.D, dest);
        annot.Put(PdfName.A, action);
        doc.GetPage(1).AddAnnotation(PdfAnnotation.MakeAnnotation(annot));

        var patched = LinkContentsFiller.ApplyAll(doc);

        patched.Should().Be(1);
        annot.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("Go to page 2");
    }
}
