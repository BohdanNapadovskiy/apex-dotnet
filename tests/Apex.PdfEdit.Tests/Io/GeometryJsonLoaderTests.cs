using Apex.PdfEdit.Core.Io;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Io;

public sealed class GeometryJsonLoaderTests
{
    private const string Form40xGeom = "form-40x-2016-Remediated/form-40x-2016-Remediated-geometry.json";

    [FactIfSample(Form40xGeom)]
    public void LoadsAllThreeSectionsForForm40x()
    {
        var g = GeometryJsonLoader.Load(TestSamples.Resolve(Form40xGeom));

        g.PageSearchText.Should().HaveCount(4);
        g.PageWordBounds.Should().HaveCount(4);
        g.PageMcidWords.Should().HaveCount(4);
        g.PageSearchText!.Keys.Should().BeEquivalentTo(new[] { "1", "2", "3", "4" });
    }

    [FactIfSample(Form40xGeom)]
    public void PageSearchTextRunsCarryTextAndChars()
    {
        var g = GeometryJsonLoader.Load(TestSamples.Resolve(Form40xGeom));
        var page1 = g.PageSearchText!["1"];
        page1.Should().NotBeEmpty();

        var first = page1[0];
        first.Text.Should().NotBeNullOrWhiteSpace();
        first.Chars.Should().NotBeNullOrEmpty();

        // Chars use JSON field `C` — the custom GlyphJsonConverter must pick it up.
        var firstChar = first.Chars![0];
        firstChar.Text.Should().NotBeNull();
        firstChar.Text.Should().HaveLength(1);
        firstChar.Height.Should().BeGreaterThan(0);
    }

    [FactIfSample(Form40xGeom)]
    public void PageWordBoundsIsFlatGlyphList()
    {
        var g = GeometryJsonLoader.Load(TestSamples.Resolve(Form40xGeom));
        var page1 = g.PageWordBounds!["1"];

        // Python probe showed 1934 glyphs on page 1 for form-40x.
        page1.Should().HaveCount(1934);

        var first = page1[0];
        first.Text.Should().Be("N");
        first.X.Should().BeApproximately(50.16, 1e-4);
        first.Y.Should().BeApproximately(748.94763, 1e-4);
        first.Width.Should().BeApproximately(7.485836, 1e-4);
        first.Height.Should().BeApproximately(13.550537, 1e-4);
    }

    [FactIfSample(Form40xGeom)]
    public void GlyphsForByPageAndMcidReturnsExpectedGlyphs()
    {
        var g = GeometryJsonLoader.Load(TestSamples.Resolve(Form40xGeom));

        var mcid0Page1 = g.GlyphsFor(1, 0);
        mcid0Page1.Should().NotBeEmpty();
        mcid0Page1[0].Text.Should().Be("N");

        // Missing page or mcid returns empty, not null.
        g.GlyphsFor(99, 0).Should().BeEmpty();
        g.GlyphsFor(1, 999999).Should().BeEmpty();
    }
}
