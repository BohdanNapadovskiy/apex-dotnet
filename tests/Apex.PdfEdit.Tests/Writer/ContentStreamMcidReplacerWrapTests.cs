using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class ContentStreamMcidReplacerWrapTests : IClassFixture<ContentStreamMcidReplacerWrapTests.FontFixture>
{
    /// <summary>xUnit fixture — one-time Helvetica load across the whole test class.</summary>
    public sealed class FontFixture
    {
        public PdfFont Helvetica { get; } = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
    }

    private readonly PdfFont _helvetica;

    public ContentStreamMcidReplacerWrapTests(FontFixture fixture)
    {
        _helvetica = fixture.Helvetica;
    }

    [Fact]
    public void ShortTextFitsOnOneLine()
    {
        var lines = ContentStreamMcidReplacer.WrapText("Hello world", _helvetica, 12f, 500f);
        lines.Should().Equal("Hello world");
    }

    [Fact]
    public void EmptyInputYieldsSingleEmptyLine()
    {
        ContentStreamMcidReplacer.WrapText("", _helvetica, 12f, 100f).Should().Equal("");
    }

    [Fact]
    public void LongTextBreaksAtWordBoundaries()
    {
        var text = "Lorem ipsum dolor sit amet";
        var lines = ContentStreamMcidReplacer.WrapText(text, _helvetica, 10f, 60f);
        lines.Count.Should().BeGreaterThan(1);
        string.Join(" ", lines).Should().Be(text);
        for (int i = 0; i < lines.Count - 1; i++)
        {
            _helvetica.GetWidth(lines[i], 10f).Should().BeLessThanOrEqualTo(60f, $"line {i} width");
        }
    }

    [Fact]
    public void OversizedSingleWordGetsItsOwnLine()
    {
        var text = "shortprefix supercalifragilisticexpialidocious tail";
        var lines = ContentStreamMcidReplacer.WrapText(text, _helvetica, 10f, 20f);
        lines.Should().HaveCount(3);
        lines[1].Should().Be("supercalifragilisticexpialidocious");
    }

    [Fact]
    public void ZeroMaxWidthShortCircuitsToSingleLine()
    {
        ContentStreamMcidReplacer.WrapText("Anything at all", _helvetica, 12f, 0f)
            .Should().Equal("Anything at all");
    }

    // effectiveFontSizeFromTfTm

    [Fact]
    public void TfWithIdentityTmYieldsRawTfSize()
    {
        var tf = TfOps("F1", 10.0);
        var tm = TmOps(1, 0, 0, 1, 0, 0);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(tf, tm).Should().Be(10f);
    }

    [Fact]
    public void TfOneWithScaledTmYieldsScaleTimesTf()
    {
        var tf = TfOps("F1", 1.0);
        var tm = TmOps(12, 0, 0, 12, 0, 0);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(tf, tm).Should().Be(12f);
    }

    [Fact]
    public void TfWithoutTmAssumesUnitScale()
    {
        var tf = TfOps("F1", 8.5);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(tf, null).Should().Be(8.5f);
    }

    [Fact]
    public void NegativeTmDIsAbsoluteValued()
    {
        var tf = TfOps("F1", 1.0);
        var tm = TmOps(10, 0, 0, -10, 0, 0);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(tf, tm).Should().Be(10f);
    }

    [Fact]
    public void MissingTfReturnsNull()
    {
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(null, null).Should().BeNull();
    }

    [Fact]
    public void NonPositiveTfReturnsNull()
    {
        var tf = TfOps("F1", 0.0);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(tf, null).Should().BeNull();
    }

    [Fact]
    public void ResultOutsideSaneRangeReturnsNull()
    {
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(TfOps("F1", 500.0), null).Should().BeNull();
        var tinyTm = TmOps(0.01, 0, 0, 0.01, 0, 0);
        ContentStreamMcidReplacer.EffectiveFontSizeFromTfTm(TfOps("F1", 1.0), tinyTm).Should().BeNull();
    }

    // effectiveLeadingMultiplier

    [Fact]
    public void LeadingMultiplierUsesResolverRatioWhenPresent()
    {
        var style = new FontStyle("Arial", 12f, "regular", "#000000", null, 1.72f);
        ContentStreamMcidReplacer.EffectiveLeadingMultiplier(style).Should().Be(1.72f);
    }

    [Fact]
    public void LeadingMultiplierFallsBackTo12WhenRatioUnset()
    {
        var style = new FontStyle("Arial", 12f, "regular", "#000000");
        ContentStreamMcidReplacer.EffectiveLeadingMultiplier(style).Should().Be(1.2f);
    }

    [Fact]
    public void LeadingMultiplierHandlesNullStyle()
    {
        ContentStreamMcidReplacer.EffectiveLeadingMultiplier(null).Should().Be(1.2f);
    }

    // segmentByRuns

    [Fact]
    public void SegmentByRunsReturnsSingleSegmentWhenNoRuns()
    {
        var primary = new FontStyle("Arial", 10f, "regular", "#000000");
        var segs = ContentStreamMcidReplacer.SegmentByRuns("hello", null, primary);
        segs.Should().HaveCount(1);
        segs[0].Text.Should().Be("hello");
        segs[0].Style.Should().BeSameAs(primary);
    }

    [Fact]
    public void SegmentByRunsReturnsSingleSegmentWhenSourceIsOneRun()
    {
        var primary = new FontStyle("Arial", 10f, "regular", "#000000");
        var sourceRuns = new List<TextRun> { new("original", primary) };
        var segs = ContentStreamMcidReplacer.SegmentByRuns("new content", sourceRuns, primary);
        segs.Should().HaveCount(1);
        segs[0].Text.Should().Be("new content");
        segs[0].Style.Should().BeSameAs(primary);
    }

    [Fact]
    public void SegmentByRunsWeavesBoldSubstringInsideRegularPrimary()
    {
        var regular = new FontStyle("Times", 12f, "regular", "#000000");
        var bold = new FontStyle("Times-Bold", 12f, "bold", "#00476B");
        var sourceRuns = new List<TextRun>
        {
            new("Generally, an ", regular),
            new("illicit discharge ", bold),
            new("is defined as: ", regular)
        };

        var segs = ContentStreamMcidReplacer.SegmentByRuns(
            "edit: an illicit discharge is now redefined as follows:",
            sourceRuns, regular);

        segs.Should().HaveCount(3);
        segs[0].Text.Should().Be("edit: an ");
        segs[0].Style.Should().BeSameAs(regular);
        segs[1].Text.Should().Be("illicit discharge ");
        segs[1].Style.Family.Should().Be("Times-Bold");
        segs[1].Style.ColorHex.Should().Be("#00476B");
        segs[2].Text.Should().Be("is now redefined as follows:");
        segs[2].Style.Should().BeSameAs(regular);
    }

    [Fact]
    public void SegmentByRunsFallsThroughWhenNoRunMatches()
    {
        var regular = new FontStyle("Times", 12f, "regular", "#000000");
        var bold = new FontStyle("Times-Bold", 12f, "bold", "#00476B");
        var sourceRuns = new List<TextRun>
        {
            new("Generally, an ", regular),
            new("illicit discharge ", bold)
        };
        var segs = ContentStreamMcidReplacer.SegmentByRuns("Completely different sentence.",
            sourceRuns, regular);
        segs.Should().HaveCount(1);
        segs[0].Style.Should().BeSameAs(regular);
    }

    [Fact]
    public void SegmentByRunsRespectsSourceOrder()
    {
        var a = new FontStyle("A", 10f, "regular", "#000000");
        var b = new FontStyle("B", 10f, "regular", "#000000");
        var sourceRuns = new List<TextRun> { new("first", a), new("second", b) };
        var segs = ContentStreamMcidReplacer.SegmentByRuns("second then first", sourceRuns, a);
        segs.Select(s => s.Text).Should().Equal("second then ", "first");
    }

    [Fact]
    public void SegmentByRunsPreservesPrimarySizeWhenRunSizeUnknown()
    {
        var primary = new FontStyle("Times", 12f, "regular", "#000000");
        var boldRaw = new FontStyle("Times-Bold", 1f, "bold", "#00476B");
        var sourceRuns = new List<TextRun>
        {
            new("plain ", primary),
            new("emphasis", boldRaw)
        };
        var segs = ContentStreamMcidReplacer.SegmentByRuns("plain emphasis here", sourceRuns, primary);
        segs.Should().HaveCount(3);
        segs[1].Style.Family.Should().Be("Times-Bold");
        segs[1].Style.Size.Should().Be(12f);
    }

    private static IList<PdfObject> TfOps(string fontName, double size)
        => new List<PdfObject> { new PdfName(fontName), new PdfNumber(size) };

    private static IList<PdfObject> TmOps(double a, double b, double c, double d, double e, double f)
        => new List<PdfObject>
        {
            new PdfNumber(a), new PdfNumber(b), new PdfNumber(c),
            new PdfNumber(d), new PdfNumber(e), new PdfNumber(f)
        };
}
