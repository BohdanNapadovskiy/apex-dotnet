using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Apex.PdfEdit.Tests;

/// <summary>
/// Diagnostic guard for a customer-reported misconfiguration: the customer set
/// <c>POC_SAMPLES_DIR</c> (or relied on the in-repo fallback) but the expected
/// corpus subfolders weren't present under it, so every corpus-gated test
/// silently skipped with <c>"Corpus sample not present: …\form-40x-2016-Remediated\…"</c>.
///
/// When <c>POC_SAMPLES_DIR</c> is unset, this test exits cleanly (nothing to
/// diagnose — a fresh clone should still ship green). When it IS set, this test
/// asserts that every critical sample referenced by the rest of the suite is
/// present at the resolved path, and fails with the full list of missing files
/// so the customer can fix their layout instead of chasing a wall of skips.
/// </summary>
public sealed class CorpusPresenceTests
{
    private readonly ITestOutputHelper _out;

    public CorpusPresenceTests(ITestOutputHelper output) => _out = output;

    private static readonly (string Dir, string[] Suffixes)[] CriticalSamples =
    {
        ("form-40x-2016-Remediated",                 new[] { ".pdf", "-document.json", "-geometry.json" }),
        ("CARE Application_Espanol-Remediated",      new[] { ".pdf", "-document.json" }),
        ("05-15-2025 Board Packet-Remediated",       new[] { ".pdf", "-document.json" }),
        ("EA Application_English-Remediated",        new[] { ".pdf", "-document.json" }),
        ("2026 Proxy 2.24.26_WEB_ADA",               new[] { ".pdf", "-document.json" }),
        ("ImplementationGuidelines-l241_Accessible", new[] { ".pdf", "-document.json", "-geometry.json", "-edits.json" }),
    };

    [Fact]
    public void ConfiguredCorpusContainsAllSamplesReferencedByTheSuite()
    {
        var env = Environment.GetEnvironmentVariable("POC_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(env))
        {
            _out.WriteLine("POC_SAMPLES_DIR not set — corpus presence check skipped.");
            return;
        }

        var root = TestSamples.Root();
        Directory.Exists(root).Should().BeTrue(
            $"POC_SAMPLES_DIR points at '{root}' but that directory does not exist. " +
            "Set POC_SAMPLES_DIR to the folder that contains the per-sample subdirectories " +
            "(e.g. 'form-40x-2016-Remediated'), not its parent.");

        var missing = new List<string>();
        foreach (var (dir, suffixes) in CriticalSamples)
        {
            var sampleDir = Path.Combine(root, dir);
            if (!Directory.Exists(sampleDir))
            {
                missing.Add($"[dir]  {sampleDir}");
                continue;
            }
            foreach (var suffix in suffixes)
            {
                var file = Path.Combine(sampleDir, dir + suffix);
                if (!File.Exists(file))
                {
                    missing.Add($"[file] {file}");
                }
            }
        }

        missing.Should().BeEmpty(
            "the following corpus files are referenced by the test suite but are missing under " +
            $"POC_SAMPLES_DIR='{root}'. Without them, the corresponding tests silently skip " +
            "with 'Corpus sample not present: …' rather than exercising the code.\n\n" +
            string.Join(Environment.NewLine, missing));
    }
}
