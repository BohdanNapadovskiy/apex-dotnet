using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class FormFieldTuFillerTests
{
    [Fact]
    public void WidgetWithoutTuGetsFieldName()
    {
        var widget = new PdfDictionary();
        widget.Put(PdfName.Subtype, PdfName.Widget);
        widget.Put(PdfName.T, new PdfString("firstName"));

        var patched = FormFieldTuFiller.PopulateIfMissing(widget, pageNumber: 3);

        patched.Should().BeTrue();
        widget.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("firstName");
    }

    [Fact]
    public void ExistingTuIsNotOverwritten()
    {
        var widget = new PdfDictionary();
        widget.Put(PdfName.Subtype, PdfName.Widget);
        widget.Put(PdfName.T, new PdfString("firstName"));
        widget.Put(PdfName.TU, new PdfString("Human-authored tooltip"));

        var patched = FormFieldTuFiller.PopulateIfMissing(widget, pageNumber: 3);

        patched.Should().BeFalse();
        widget.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("Human-authored tooltip");
    }

    [Fact]
    public void NonWidgetSubtypeSkipped()
    {
        var annot = new PdfDictionary();
        annot.Put(PdfName.Subtype, PdfName.Link);
        annot.Put(PdfName.T, new PdfString("someName"));

        var patched = FormFieldTuFiller.PopulateIfMissing(annot, pageNumber: 1);

        patched.Should().BeFalse();
        annot.Get(PdfName.TU).Should().BeNull();
    }

    [Fact]
    public void ParentChainYieldsFullyQualifiedName()
    {
        var root = new PdfDictionary();
        root.Put(PdfName.T, new PdfString("Address"));

        var mid = new PdfDictionary();
        mid.Put(PdfName.T, new PdfString("Home"));
        mid.Put(PdfName.Parent, root);

        var widget = new PdfDictionary();
        widget.Put(PdfName.Subtype, PdfName.Widget);
        widget.Put(PdfName.T, new PdfString("Street"));
        widget.Put(PdfName.Parent, mid);

        var patched = FormFieldTuFiller.PopulateIfMissing(widget, pageNumber: 1);

        patched.Should().BeTrue();
        widget.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("Address.Home.Street");
    }

    [Fact]
    public void MissingFieldNameFallsBackToPageLabel()
    {
        var widget = new PdfDictionary();
        widget.Put(PdfName.Subtype, PdfName.Widget);

        var patched = FormFieldTuFiller.PopulateIfMissing(widget, pageNumber: 7);

        patched.Should().BeTrue();
        widget.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("Form field on page 7");
    }

    [Fact]
    public void CyclicParentChainDoesNotHang()
    {
        // Malformed source: root.Parent → root.
        var root = new PdfDictionary();
        root.Put(PdfName.T, new PdfString("Only"));
        root.Put(PdfName.Parent, root);

        var widget = new PdfDictionary();
        widget.Put(PdfName.Subtype, PdfName.Widget);
        widget.Put(PdfName.T, new PdfString("Leaf"));
        widget.Put(PdfName.Parent, root);

        var patched = FormFieldTuFiller.PopulateIfMissing(widget, pageNumber: 1);

        patched.Should().BeTrue();
        widget.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("Only.Leaf");
    }
}
