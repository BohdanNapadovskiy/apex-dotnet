using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

public sealed class SourcePdfFontResolverTests
{
    private const string Form40xPdf = "form-40x-2016-Remediated/form-40x-2016-Remediated.pdf";
    private const string ImplGuidelinesPdf = "ImplementationGuidelines-l241_Accessible/ImplementationGuidelines-l241_Accessible.pdf";

    [FactIfSample(Form40xPdf)]
    public void ResolvesFontStyleForKnownPageMcid()
    {
        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(Form40xPdf));
        var style = resolver.Resolve(1, 0);
        style.Should().NotBeNull();

        style!.Family.Should().NotBeNullOrWhiteSpace();
        style.Size.Should().BeInRange(6.0f, 72.0f);
        style.Weight.Should().BeOneOf("regular", "medium", "light", "bold");
        style.ColorHex.Should().MatchRegex("#[0-9A-F]{6}");
    }

    [FactIfSample(Form40xPdf)]
    public void ResolveReturnsEmptyForMissingKeys()
    {
        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(Form40xPdf));
        resolver.Resolve(999, 0).Should().BeNull();
        resolver.Resolve(1, -1).Should().BeNull();
        resolver.Resolve(1, 99999).Should().BeNull();
    }

    [FactIfSample(Form40xPdf)]
    public void IndexCoversMultipleMcidsAcrossPages()
    {
        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(Form40xPdf));
        resolver.Resolve(1, 0);
        resolver.KnownEntryCount().Should().BeGreaterThan(50);
    }

    /// <summary>
    /// Regression for the customer report on ImplementationGuidelines: the source's Courier
    /// subset has cmap holes at 'Z' and '+', so the strict guard refuses any setText with
    /// those characters. The OCR raster stamper can still draw them (extracted TTF has the
    /// outlines). The widened variant must accept both; the strict variant must still refuse them.
    /// </summary>
    [FactIfSample(ImplGuidelinesPdf)]
    public void WidenedGuardAcceptsGlyphsWhoseEncodingIsMissingButOutlineExists()
    {
        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(ImplGuidelinesPdf));
        const string probe = "APEX TESTING RESULTS - TODAY - EGJKM+MQWXYZ";

        // Strict: fails on the first char not in the encoding cmap ('+').
        var strict = resolver.FirstUnrenderableCodePoint(3, 1, probe);
        strict.Should().NotBeNull();
        strict!.Value.Should().BeOneOf((int)'+', (int)'Z');

        // Widened: rescues '+' and 'Z' via the SkiaSharp-extracted glyph outlines.
        resolver.FirstUnrenderableCodePointForOcrRaster(3, 1, probe).Should().BeNull();

        // Isolated single-char calls must also succeed.
        resolver.FirstUnrenderableCodePointForOcrRaster(3, 1, "+").Should().BeNull(
            "widened guard accepts '+' in isolation");
        resolver.FirstUnrenderableCodePointForOcrRaster(3, 1, "Z").Should().BeNull(
            "widened guard accepts 'Z' in isolation");
    }
}
