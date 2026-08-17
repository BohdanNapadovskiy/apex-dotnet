namespace Apex.PdfEdit.Tests;

/// <summary>
/// Resolves the output root where tests persist rendered PDFs for manual inspection.
/// Mirrors <see cref="TestSamples"/>'s resolution chain so an external layout can live
/// alongside the repo instead of inside <c>bin/</c>.
///
/// Resolution order:
/// <list type="number">
///   <item>Environment variable <c>POC_EDIT_DIR</c></item>
///   <item>Fallback: in-repo <c>bin/</c> (so a fresh clone still works with no setup).</item>
/// </list>
/// </summary>
public static class TestOutputs
{
    public static string Root()
    {
        var env = Environment.GetEnvironmentVariable("POC_EDIT_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return "bin";
    }

    /// <summary>Return (and create) the per-sample output directory.</summary>
    public static string ForSample(string sample)
    {
        var dir = Path.Combine(Root(), sample);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
