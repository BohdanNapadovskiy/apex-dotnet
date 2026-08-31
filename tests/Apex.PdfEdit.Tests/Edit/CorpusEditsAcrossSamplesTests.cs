using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace Apex.PdfEdit.Tests.Edit;

/// <summary>
/// End-to-end coverage across every sample in the corpus.
///
/// For each folder under <c>POC_SAMPLES_DIR</c> that contains a matching
/// <c>{name}.pdf</c>, <c>{name}-document.json</c>, <c>{name}-geometry.json</c>,
/// and <c>{name}-edits.json</c>, this theory loads the trio, runs
/// <see cref="EditEngine"/> → writer, and persists the result to
/// <c>{POC_EDIT_DIR}/{name}/{name}_edit.pdf</c> so a reviewer can eyeball the
/// corpus-wide output.
///
/// Writer selection mirrors the CLI's <c>edit-ocr</c> auto-fallback: when
/// <see cref="ScanDetector.IsScannedOcr"/> returns true the sample is routed
/// through <see cref="OcrRevectorizeWriter"/> (with the widened glyph guard on
/// the engine), otherwise through <see cref="SourceBasedWriter"/>. Without this
/// split, scan-only docs like <c>ImplementationGuidelines-l241_Accessible</c>
/// would copy the source raster verbatim and the edits wouldn't show visually.
///
/// Complements the per-sample focused tests in <see cref="EditEngineTests"/> —
/// those assert exact byte/tree invariants for specific bug fixes; this one keeps
/// the whole corpus honest against silent regressions in any of the four edit ops.
///
/// The theory skips cleanly when the corpus is missing (empty test-case row) so a
/// fresh clone with no corpus still ships green.
/// </summary>
public sealed class CorpusEditsAcrossSamplesTests
{
    private readonly ITestOutputHelper _out;

    public CorpusEditsAcrossSamplesTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Samples()
    {
        var root = TestSamples.Root();
        // Always emit at least one row so xUnit doesn't fail the theory with
        // "No data found". When the corpus isn't present locally, emit the
        // sentinel <"__missing__"> and skip inside the body via the same
        // FactIfSampleAttribute convention used elsewhere in this suite.
        if (!Directory.Exists(root))
        {
            yield return new object[] { "__missing__" };
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            var pdf = Path.Combine(dir, name + ".pdf");
            var doc = Path.Combine(dir, name + "-document.json");
            var geom = Path.Combine(dir, name + "-geometry.json");
            var edits = Path.Combine(dir, name + "-edits.json");
            if (File.Exists(pdf) && File.Exists(doc) && File.Exists(geom) && File.Exists(edits))
            {
                yield return new object[] { name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EndToEndAppliesEditsAndProducesPdf(string sample)
    {
        if (sample == "__missing__")
        {
            // Corpus not present at POC_SAMPLES_DIR — exit cleanly. xUnit 2.9
            // has no Assert.Skip; the row shows as passing with the log below.
            _out.WriteLine("Corpus not present at POC_SAMPLES_DIR — corpus-wide sweep skipped.");
            return;
        }

        var pdfPath = TestSamples.Resolve(sample, sample + ".pdf");
        var docPath = TestSamples.Resolve(sample, sample + "-document.json");
        var geomPath = TestSamples.Resolve(sample, sample + "-geometry.json");
        var editsPath = TestSamples.Resolve(sample, sample + "-edits.json");

        var doc = DocumentJsonLoader.Load(docPath);
        var geom = GeometryJsonLoader.Load(geomPath);
        var edits = EditsJsonLoader.Load(editsPath);
        edits.Operations.Should().NotBeEmpty($"{sample}: edits.json declared no operations");

        var outBuf = new MemoryStream();
        EditResult result;
        bool isOcr;
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            // Auto-route between writers the same way CLI edit-ocr does. On a
            // scan-only PDF the source raster is authoritative, so
            // SourceBasedWriter would produce a visually unchanged output.
            isOcr = ScanDetector.IsScannedOcr(resolver.SourceDocument);
            result = new EditEngine(resolver, allowExtractedGlyphs: isOcr).Apply(doc, geom, edits);
            if (isOcr)
            {
                new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, outBuf);
            }
            else
            {
                new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
            }
        }

        // Writers dispose the stream, so snapshot bytes up front — downstream
        // length checks would throw ObjectDisposedException otherwise.
        var pdfBytes = outBuf.ToArray();

        // Persist the rendered PDF next to the other samples for manual inspection.
        // Under POC_EDIT_DIR this lands as C:\projects\pdf\apex\edit\<sample>\<sample>_edit.pdf.
        var debugDir = TestOutputs.ForSample(sample);
        var debugPath = Path.Combine(debugDir, sample + "_edit.pdf");
        File.WriteAllBytes(debugPath, pdfBytes);

        _out.WriteLine(
            $"{sample}: writer={(isOcr ? "OcrRevectorize" : "SourceBased")} · " +
            $"applied {result.AppliedOpIds.Count}/{edits.Operations.Count} ops · " +
            $"issues={result.Issues.Count} · " +
            $"setText={result.Plan.SetTextOverlays.Count} " +
            $"addPara={result.Plan.AddParagraphOverlays.Count} " +
            $"addLI={result.Plan.AddListItemOverlays.Count} " +
            $"delete={result.Plan.DeleteOverlays.Count} " +
            $"moves={result.Plan.MoveOverlays.Count} · " +
            $"out={debugPath}");
        foreach (var issue in result.Issues)
        {
            _out.WriteLine($"  [issue] op={issue.OpId} type={issue.OpType}: {issue.Message}");
        }

        // Sanity — the overall pipeline must produce SOME successful ops and a
        // readable PDF for every sample. Per-op failures are surfaced via the
        // WriteLine log above so a reviewer can diagnose them without failing the
        // whole sweep on one bad target — the focused per-sample tests already
        // guard the specific invariants we care about.
        result.AppliedOpIds.Should().NotBeEmpty(
            $"{sample}: zero ops applied — issues: " +
            string.Join(" | ", result.Issues.Select(i => $"{i.OpId}:{i.Message}")));

        pdfBytes.Length.Should().BeGreaterThan(0, $"{sample}: writer produced empty PDF");

        using var reader = new PdfReader(new MemoryStream(pdfBytes));
        using var pdf = new PdfDocument(reader);
        pdf.GetNumberOfPages().Should().BeGreaterThan(0, $"{sample}: output PDF has no pages");
    }
}
