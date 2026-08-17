using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Behavioural tests for the cross-platform system font search. Full-load tests run
/// only on the current OS (they hit real font files on disk); classification tests
/// are OS-agnostic and always run.
/// </summary>
public sealed class SystemFontLocatorTests
{
    [FactOnOs("Windows")]
    public void WindowsLocatesArialRegular()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("Arial", 12f, "regular", "#000000"));
        p.Should().NotBeNull();
        p!.ToLowerInvariant().Should().EndWith("arial.ttf");
    }

    [FactOnOs("Windows")]
    public void WindowsLoadsBoldViaWeight()
    {
        var font = SystemFontLocator.Load("Verdana", "bold");
        font.Should().NotBeNull();
    }

    [FactOnOs("Windows")]
    public void WindowsLocateFileHonoursWeightForArialBucket()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("Arial", 12f, "bold", "#000000"));
        p.Should().NotBeNull();
        p!.ToLowerInvariant().Should().EndWith("arialbd.ttf");
    }

    [FactOnOs("Windows")]
    public void WindowsLocateFileHonoursBlackWeight()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("Arial Black", 12f, "regular", "#000000"));
        p.Should().NotBeNull();
        p!.ToLowerInvariant().Should().EndWith("arialbd.ttf");
    }

    [FactOnOs("Windows")]
    public void WindowsUniversalFallbackForUnknownSerifFamilyPicksTimes()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("SomeUnknownSerifPS", 12f, "regular", "#000000"));
        p.Should().NotBeNull();
        p!.ToLowerInvariant().Should().EndWith("times.ttf");
    }

    [FactOnOs("Windows")]
    public void WindowsUnknownFamilyDefaultsToArial()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("BentonSansCond", 12f, "regular", "#000000"));
        p.Should().NotBeNull();
        p!.ToLowerInvariant().Should().EndWith("arial.ttf");
    }

    [FactOnOs("Mac")]
    public void MacLocatesArialInSupplementalOrHelveticaTtc()
    {
        var p = SystemFontLocator.LocateFile(new FontStyle("Arial", 12f, "regular", "#000000"));
        p.Should().NotBeNull();
        p!.Should().Match(s => s.Contains("Arial") || s.Contains("Helvetica"));
    }

    [FactOnOs("Linux")]
    public void LinuxLoadsLiberationSansForArialBucketIfInstalled()
    {
        // Liberation isn't guaranteed on every distro — assert either it resolved or resolved
        // empty. Don't fail on an unpopulated CI.
        var p = SystemFontLocator.LocateFile(new FontStyle("Arial", 12f, "regular", "#000000"));
        if (p is not null)
        {
            p.Should().Match(s => s.Contains("Liberation") || s.Contains("Arial") || s.Contains("DejaVu"));
        }
    }

    [Fact]
    public void NullOrBlankFamilyDoesNotCrash()
    {
        // Just prove neither throws — return values are OS-dependent.
        _ = SystemFontLocator.LocateFile(new FontStyle(null, 12f, "regular", "#000000"));
        SystemFontLocator.Load(null, "regular").Should().BeNull();
        SystemFontLocator.Load("", "regular").Should().BeNull();
    }
}
