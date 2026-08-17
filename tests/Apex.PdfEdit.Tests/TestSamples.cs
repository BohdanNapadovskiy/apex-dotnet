namespace Apex.PdfEdit.Tests;

/// <summary>
/// Resolves the customer-supplied corpus root for tests.
///
/// The corpus is not tracked in git (~40 MB of PDFs + JSON). Resolution order:
/// <list type="number">
///   <item>Environment variable <c>POC_SAMPLES_DIR</c></item>
///   <item>Fallback: the historical in-repo path <c>SamplePDFs-ExtracredJSON/SamplePDFs-ExtracredJSON</c>
///         — kept so a fresh clone with the corpus dropped back in-place still works.</item>
/// </list>
/// All sample-dependent tests use <see cref="FactIfSampleAttribute"/> / <see cref="TheoryIfSampleAttribute"/>
/// so a missing corpus causes them to skip cleanly rather than fail.
/// </summary>
public static class TestSamples
{
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("POC_SAMPLES_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine("SamplePDFs-ExtracredJSON", "SamplePDFs-ExtracredJSON");
    }

    public static string Resolve(params string[] parts)
        => Path.Combine(new[] { Root() }.Concat(parts).ToArray());
}
