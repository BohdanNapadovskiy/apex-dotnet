using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class PageFontInventoryTests
{
    private const string Form40xPdf = "form-40x-2016-Remediated/form-40x-2016-Remediated.pdf";

    [Fact]
    public void FamilyStemStripsSubsetPrefixAndWeightSuffix()
    {
        PageFontInventory.FamilyStem("VHIJCY+Verdana").Should().Be("verdana");
        PageFontInventory.FamilyStem("PYGUWK+Verdana,Bold").Should().Be("verdana");
        PageFontInventory.FamilyStem("EJGEHM+TimesNewRomanPSMT").Should().Be("timesnewromanpsmt");
        PageFontInventory.FamilyStem("EJGEJN+TimesNewRomanPS-BoldMT").Should().Be("timesnewromanps");
        PageFontInventory.FamilyStem("Helvetica-Bold").Should().Be("helvetica");
        PageFontInventory.FamilyStem(null).Should().BeEmpty();
    }

    [FactIfSample(Form40xPdf)]
    public void EnumeratesAllPage1FontsForForm40x()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        using var reader = new PdfReader(srcPath);
        using var pdf = new PdfDocument(reader);
        var inv = PageFontInventory.Of(pdf.GetPage(1));
        // Form-40x page 1 has 6 embedded Type0 fonts: Verdana + Verdana-Bold + Webdings + 3 Times.
        inv.Fonts.Should().HaveCount(6);
    }

    [FactIfSample(Form40xPdf)]
    public void FindsVerdanaRegularAndBoldSeparately()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        using var reader = new PdfReader(srcPath);
        using var pdf = new PdfDocument(reader);
        var inv = PageFontInventory.Of(pdf.GetPage(1));

        var regular = inv.FindByFamilyAndWeight("VHIJCY+Verdana", "regular");
        var bold = inv.FindByFamilyAndWeight("VHIJCY+Verdana", "bold");

        regular.Should().NotBeNull();
        bold.Should().NotBeNull();
        regular.Should().NotBeSameAs(bold);
    }

    [FactIfSample(Form40xPdf)]
    public void CanRenderIsTrueForOriginalTextAndFalseForUnusedGlyph()
    {
        var srcPath = TestSamples.Resolve(Form40xPdf);
        using var reader = new PdfReader(srcPath);
        using var pdf = new PdfDocument(reader);
        var inv = PageFontInventory.Of(pdf.GetPage(1));
        var verdana = inv.FindByFamilyAndWeight("VHIJCY+Verdana", "regular");
        verdana.Should().NotBeNull();

        PageFontInventory.CanRender(verdana!, "North Dakota").Should().BeTrue("original glyphs present");
        PageFontInventory.CanRender(verdana!, "税").Should().BeFalse("out-of-subset glyph refused");
    }
}
