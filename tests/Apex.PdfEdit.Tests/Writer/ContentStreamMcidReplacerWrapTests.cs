using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Layout;
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

    // justifiedTw

    [Fact]
    public void JustifiedTwLastLineReturnsBaseTw()
    {
        // Typographic convention: the paragraph's final line stays left-anchored, so no
        // extra word spacing regardless of how short the line is.
        ContentStreamMcidReplacer.JustifiedTw(
            "trailing", _helvetica, 12f, 500f, baseTc: 0.0, baseTw: 0.5, isLastLine: true)
            .Should().Be(0.5);
    }

    [Fact]
    public void JustifiedTwLineWithoutSpacesReturnsBaseTw()
    {
        ContentStreamMcidReplacer.JustifiedTw(
            "singleword", _helvetica, 12f, 500f, baseTc: 0.0, baseTw: 0.0, isLastLine: false)
            .Should().Be(0.0);
    }

    [Fact]
    public void JustifiedTwStretchesShortLineToBboxWidth()
    {
        // Slack per space stays inside the fontSize/2 sanity cap → helper divides it evenly.
        var line = "aa bb cc";
        float fontSize = 12f;
        int spaces = line.Count(c => c == ' ');
        double perSpace = 5.0;
        double natural = _helvetica.GetWidth(line, fontSize);
        float bboxWidth = (float)(natural + perSpace * spaces);
        double tw = ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize, bboxWidth, baseTc: 0.0, baseTw: 0.0, isLastLine: false);
        tw.Should().BeApproximately(perSpace, 0.001);
    }

    [Fact]
    public void JustifiedTwStretchesNonFinalLineEvenWithWideGaps()
    {
        // A mid-paragraph line that broke early before a long word must still reach the
        // right edge (Feedback 1.4: the old fontSize/2 cap left it ragged, reading as LEFT).
        const string line = "a b";
        double natural = _helvetica.GetWidth(line, 12f);
        double tw = ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize: 12f, bboxWidth: 240f,
            baseTc: 0.0, baseTw: 0.0, isLastLine: false);
        tw.Should().BeApproximately(240.0 - natural, 0.001);
    }

    [Fact]
    public void SegmentEndLineIndicesMarksLinesBeforeHardBreaks()
    {
        const string text = "one two three four five six\nseven eight\nnine";
        float width = _helvetica.GetWidth("one two three", 12f) + 1f;
        var lines = ContentStreamMcidReplacer.WrapText(text, _helvetica, 12f, width);
        lines.Should().Equal("one two three", "four five six", "seven eight", "nine");
        ContentStreamMcidReplacer.SegmentEndLineIndices(text, _helvetica, 12f, width, 0.0, lines.Count)
            .Should().BeEquivalentTo(new[] { 1, 2 }, "the final line is handled by isLastLine");
    }

    [Fact]
    public void JustifiedTwZeroExtraReturnsBaseTw()
    {
        // Line already fills the bbox → no inflation needed.
        var line = "quick brown fox";
        float fontSize = 12f;
        float bboxWidth = (float)_helvetica.GetWidth(line, fontSize);
        ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize, bboxWidth, baseTc: 0.0, baseTw: 0.25, isLastLine: false)
            .Should().Be(0.25);
    }

    // space compression (Feedback 1.3)

    [Fact]
    public void WrapTextPacksOneMoreWordWhenSpacesMayShrink()
    {
        // "aa bb cc" misses the bbox by 1pt at natural spacing; 0.6pt off each of its two
        // spaces makes it fit.
        float fontSize = 12f;
        float maxWidth = _helvetica.GetWidth("aa bb cc", fontSize) - 1f;
        ContentStreamMcidReplacer.WrapText("aa bb cc", _helvetica, fontSize, maxWidth)
            .Should().Equal("aa bb", "cc");
        ContentStreamMcidReplacer.WrapText("aa bb cc", _helvetica, fontSize, maxWidth, shrinkPerSpace: 0.6)
            .Should().Equal("aa bb cc");
        ContentStreamMcidReplacer.WrapText("aa bb cc", _helvetica, fontSize, maxWidth, shrinkPerSpace: 0.4)
            .Should().Equal("aa bb", "cc");
    }

    [Fact]
    public void JustifiedTwCompressesOverfullLineWithinAllowance()
    {
        var line = "aa bb cc";
        float fontSize = 12f;
        float bboxWidth = _helvetica.GetWidth(line, fontSize) - 1f;
        // Needs -0.5pt per space; allowed.
        ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize, bboxWidth, baseTc: 0.0, baseTw: 0.0, isLastLine: false, maxShrinkPerSpace: 0.6)
            .Should().BeApproximately(-0.5, 0.001);
        // The last line is compressed too — the wrap may have packed it the same way.
        ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize, bboxWidth, baseTc: 0.0, baseTw: 0.0, isLastLine: true, maxShrinkPerSpace: 0.6)
            .Should().BeApproximately(-0.5, 0.001);
        // Never beyond the allowance.
        ContentStreamMcidReplacer.JustifiedTw(
            line, _helvetica, fontSize, bboxWidth, baseTc: 0.0, baseTw: 0.0, isLastLine: false, maxShrinkPerSpace: 0.2)
            .Should().BeApproximately(-0.2, 0.001);
    }

    [Fact]
    public void JustifiedTwLastLineStaysUnstretchedWhenShrinkAllowed()
    {
        ContentStreamMcidReplacer.JustifiedTw(
            "aa bb cc", _helvetica, 12f, 500f, baseTc: 0.0, baseTw: 0.0, isLastLine: true, maxShrinkPerSpace: 0.6)
            .Should().Be(0.0);
    }

    [Fact]
    public void SpaceShrinkOnlyWhenJustifiedSourceCompressedItsSpaces()
    {
        float fontSize = 9f;
        double natural = _helvetica.GetWidth(" ", fontSize);
        SetTextOverlay Overlay(Alignment alignment, double minGap) => new(
            1, 0, 0, 400, 30, "text", new FontStyle("Helvetica", 9f, "normal", "#000000"), alignment, "n", 0,
            double.NaN, double.NaN, Array.Empty<TextRun>(), SourceMinWordGap: minGap);

        // Source tightest line 0.3pt under natural → that much plus headroom.
        ContentStreamMcidReplacer.SpaceShrinkPt(Overlay(Alignment.Justified, natural - 0.3), _helvetica, fontSize)
            .Should().BeApproximately(0.345, 0.001);
        // Source at or above natural spacing, unknown, or not justified → never compress.
        ContentStreamMcidReplacer.SpaceShrinkPt(Overlay(Alignment.Justified, natural + 1.0), _helvetica, fontSize)
            .Should().Be(0.0);
        ContentStreamMcidReplacer.SpaceShrinkPt(Overlay(Alignment.Justified, double.NaN), _helvetica, fontSize)
            .Should().Be(0.0);
        ContentStreamMcidReplacer.SpaceShrinkPt(Overlay(Alignment.Left, natural - 0.3), _helvetica, fontSize)
            .Should().Be(0.0);
        // Capped at a quarter of the space however tight the source looks.
        ContentStreamMcidReplacer.SpaceShrinkPt(Overlay(Alignment.Justified, natural * 0.2), _helvetica, fontSize)
            .Should().BeApproximately(natural * 0.25, 0.001);
    }

    // isHeadingOrHeaderTag

    [Theory]
    [InlineData("H1", true)]
    [InlineData("H2", true)]
    [InlineData("H3", true)]
    [InlineData("H4", true)]
    [InlineData("H5", true)]
    [InlineData("H6", true)]
    [InlineData("TH", true)]
    [InlineData("P", false)]
    [InlineData("Note", false)]
    [InlineData("Link", false)]
    [InlineData("LBody", false)]
    [InlineData("TD", false)]          // table data cell — not TH
    [InlineData("Hx", false)]          // invalid heading level
    [InlineData("H", false)]           // just 'H'
    [InlineData("H12", false)]         // > 6
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHeadingOrHeaderTagRecognisesTheExpectedShapes(string? tag, bool expected)
    {
        ContentStreamMcidReplacer.IsHeadingOrHeaderTag(tag).Should().Be(expected);
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
