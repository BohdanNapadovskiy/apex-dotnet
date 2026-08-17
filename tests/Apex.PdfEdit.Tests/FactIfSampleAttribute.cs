using Xunit;

namespace Apex.PdfEdit.Tests;

/// <summary>
/// xUnit equivalent of Java's <c>@EnabledIf</c> — skips the test when the given
/// path (relative to the corpus root) does not exist. Mirrors the Java tests'
/// pattern of gating corpus-dependent facts.
/// </summary>
public sealed class FactIfSampleAttribute : FactAttribute
{
    public FactIfSampleAttribute(string relativePath)
    {
        var full = TestSamples.Resolve(relativePath);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            Skip = $"Corpus sample not present: {full}";
        }
    }
}
