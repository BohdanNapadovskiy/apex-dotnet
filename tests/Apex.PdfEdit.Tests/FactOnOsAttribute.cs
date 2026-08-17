using System.Runtime.InteropServices;
using Xunit;

namespace Apex.PdfEdit.Tests;

/// <summary>
/// xUnit equivalent of JUnit's <c>@EnabledOnOs(OS.WINDOWS)</c> — skips the test when
/// the current OS doesn't match. <paramref name="osName"/> is one of
/// <c>"Windows"</c> / <c>"OSX"</c> / <c>"Linux"</c> (case-insensitive; <c>"Mac"</c>
/// alias accepted too).
/// </summary>
public sealed class FactOnOsAttribute : FactAttribute
{
    public FactOnOsAttribute(string osName)
    {
        var target = osName.Equals("Mac", StringComparison.OrdinalIgnoreCase) ? "OSX" : osName;
        var platform = target switch
        {
            var s when s.Equals("Windows", StringComparison.OrdinalIgnoreCase) => OSPlatform.Windows,
            var s when s.Equals("OSX", StringComparison.OrdinalIgnoreCase) => OSPlatform.OSX,
            var s when s.Equals("Linux", StringComparison.OrdinalIgnoreCase) => OSPlatform.Linux,
            _ => throw new ArgumentException($"Unknown OS name: {osName}. Use Windows/OSX/Linux.", nameof(osName))
        };
        if (!RuntimeInformation.IsOSPlatform(platform))
        {
            Skip = $"Only runs on {target}; current OS is {RuntimeInformation.OSDescription}";
        }
    }
}
