using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;

namespace Apex.PdfEdit.Tests.Edit;

/// <summary>
/// Regression fixtures for the Ram-supplied Campus Housing UAT report (2026-09-07).
/// The report flagged five distinct behaviours in the shipped OutputFilePDF.pdf; four
/// have since been fixed in-tree and this file locks them in.
///
/// <list type="number">
///   <item><b>Alignment (P0, 2026-09-07)</b> — a justified source paragraph was rendered
///       left-aligned after <c>setText</c>. Fixed by per-line Tw inflation in
///       <see cref="ContentStreamMcidReplacer"/>. Guarded by
///       <see cref="JustifiedParagraphKeepsRightEdgeAlignment"/>.</item>
///   <item><b>Paragraph drop</b> — the "Building anti-racist" body paragraph was omitted
///       entirely from the output. Fixed by <see cref="MultiFontLineEmitter"/> fallback
///       chain (commit a7648c3). Guarded by <see cref="RacismParagraphBodyIsRendered"/>.</item>
///   <item><b>TOC entry drop</b> — a TOC row ("7.0 Keys and locks ... 10") was omitted.
///       Fixed by the same fallback chain. Guarded by <see cref="TocKeysAndLocksLineIsRendered"/>.</item>
///   <item><b>Heading drift</b> — the modified H3 ("3.4 Deposits and initial payments")
///       drifted upward into the fee table above it. Fixed by the overflow guard in
///       <see cref="ContentStreamMcidReplacer"/> <c>EmitReplacement</c>. Guarded by
///       <see cref="ModifiedHeadingStaysAtSourcePosition"/>.</item>
/// </list>
///
/// The fifth UAT item ("One or more edits could not be applied") was already softened
/// pre-report by <see cref="EditEngine"/> <c>CheckFontCoverage</c>; its new behaviour
/// is covered by <see cref="CustomerFeedbackRegressionTests"/>.
/// </summary>
public sealed class RamCampusHousingRegressionTests
{
    private const string RamDir = "Ram PDF files/Ram PDF files";
    private const string RamPdf = RamDir + "/InputFilePDF.pdf";
    private const string RamDoc = RamDir + "/document.json";

    /// <summary>
    /// The 12 setTexts from Ram's <c>edits.json</c> (2026-09-07), transcribed verbatim
    /// from the customer's <c>{targetNodeId, updatedText}</c> schema into engine-format
    /// <see cref="SetTextOp"/> instances. Left inline (rather than loading the JSON
    /// file) so a reviewer can compare byte-for-byte against the customer report
    /// without traversing a converter.
    /// </summary>
    private static EditsJson BuildRamEdits()
    {
        var edits = new EditsJson { SchemaVersion = "1" };
        var raw = new (string Target, string Content)[]
        {
            ("118", "1.6 Documents Required - Test content added here. "),
            ("123", "marriage certificate, or  Test content added here. "),
            ("13",
                "We know that racism, racist attitudes, and systemic racism exists, and our campus and residences are not immune to those behaviours and structures We deeply value the diverse contributions of Black, Test content added here. Indigenous, racialized, 2SLGBTQIA+ students and employees, and all students with disabilities We are committed to addressing racism and developing an anti-racist community Let us be clear, prejudice or hate of any kind has no place in our community To see change, we need to acknowledge and dismantle racial biases in our residence culture, structures, and practices We will respond to racism and discrimination We will engage in uncomfortable conversations We are prepared to learn and help others learn Because racial equity and inclusion matter."),
            ("163", "2.0 Residence contract - Test content added here. "),
            ("164", "2.1 Contract period Test content added here. "),
            ("207", "Advantage B - Test content added here. "),
            ("215", "3.4 Deposits and initial payments - Test content added here. "),
            ("39",  "7.0 Keys and locks Test content added here. 10"),
            ("5",
                "This document outlines the terms and conditions of your Student Family Housing residence contract with Campus Housing for 2025 2026 (the \u201CAgreement\u201D). Text content added here. "),
            ("68",  "1.0 Residence eligibility - Test content added here. "),
            ("8",
                "Although every effort has been made to ensure the accuracy of information, it may be subject to correction or change without notice These terms and conditions are only for Campus Housing and not the Affiliated University Colleges. Test content added here."),
            ("92",  "1.3 Degree completion - Test content added here. "),
        };
        for (int i = 0; i < raw.Length; i++)
        {
            edits.Operations.Add(SetTextOp.Of("ram-edit-" + i, raw[i].Target, raw[i].Content));
        }
        return edits;
    }

    private static byte[] RunPipeline()
    {
        var docPath = TestSamples.Resolve(RamDoc);
        var pdfPath = TestSamples.Resolve(RamPdf);
        var doc = DocumentJsonLoader.Load(docPath);
        var edits = BuildRamEdits();
        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty("all 12 Ram setTexts must apply cleanly");
            result.AppliedOpIds.Should().HaveCount(edits.Operations.Count);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }
        return outBuf.ToArray();
    }

    [FactIfSample(RamPdf)]
    public void JustifiedParagraphKeepsRightEdgeAlignment()
    {
        // Nodes 5, 8, 13, 39 on page 1 are JUSTIFIED source paragraphs whose classified
        // right edge sits at ~527pt (body column mode-right). Pre-P0 the writer emitted
        // no Tw, so wrapped lines fell short by tens of points. Post-P0 non-last lines
        // should reach the right edge within ~5pt.
        var bytes = RunPipeline();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);

        const double rightEdge = 527.0;
        const double tolerance = 5.0;
        // Track the max endX per baseline-Y. A whole line contributes several
        // TextRenderInfo events; the rightmost one is the line end.
        var lineEnds = new Dictionary<double, double>();
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (string.IsNullOrWhiteSpace(s)) return;
            var end = tri.GetBaseline().GetEndPoint();
            double y = Math.Round(end.Get(1), 1);
            double x = end.Get(0);
            if (!lineEnds.TryGetValue(y, out var cur) || x > cur) lineEnds[y] = x;
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(1));

        var linesReachingRightEdge = lineEnds.Values.Count(x => x >= rightEdge - tolerance);
        linesReachingRightEdge.Should().BeGreaterThanOrEqualTo(3,
            "P0: edited justified paragraphs must re-emit Tw so wrapped lines fill the bbox");
    }

    [FactIfSample(RamPdf)]
    public void RacismParagraphBodyIsRendered()
    {
        // Pre-a7648c3 the writer dropped chars not in the source font subset, which for
        // this paragraph meant the entire body was skipped. Fixed by MultiFontLineEmitter
        // fallback. Assert distinctive substrings survive.
        var bytes = RunPipeline();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().Contain("racist attitudes",
            "node 13 body must render — this is the exact string the UAT screenshot showed missing");
        page1.Should().Contain("2SLGBTQIA",
            "distinctive token proves the mid-paragraph run reached the writer");
        page1.Should().Contain("racial equity",
            "tail of node 13 must render — pre-fix the paragraph was truncated");
    }

    [FactIfSample(RamPdf)]
    public void TocKeysAndLocksLineIsRendered()
    {
        // Node 39 is the "7.0 Keys and locks ... 10" TOC row on page 1. Pre-fix the
        // entire row was omitted (image9.png in the UAT deck).
        var bytes = RunPipeline();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        var page1 = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        page1.Should().Contain("Keys and locks",
            "TOC entry for node 39 must render after setText adds new characters");
    }

    [FactIfSample(RamPdf)]
    public void TocRowKeepsPagenumRightAnchored()
    {
        // P2: the customer's edit adds "Test content added here." between the title and
        // the pagenum ("7.0 Keys and locks Test content added here. 10"). Pre-P2 that
        // was emitted as a single left-anchored string and the source's dot leaders
        // still rendered at their original X — visual pile-up. The TOC-row re-emit
        // splits title / dots / pagenum: title at the left, fresh dots, "10"
        // right-anchored near the body-column right edge (~527pt).
        var bytes = RunPipeline();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);

        var chunks = CollectRowChunks(pdf, page: 1, needle: "Keys and locks");
        chunks.Should().NotBeEmpty("expected to find the 'Keys and locks' TOC row on page 1");

        chunks[0].StartX.Should().BeApproximately(85.0, 3.0,
            "the TOC title should start at the body column's left margin");

        var pagenumChunk = chunks.LastOrDefault(c => c.Text.Trim() == "10");
        pagenumChunk.Text.Should().Be("10",
            "the pagenum should be emitted as its own text run at the row's right edge");
        pagenumChunk.EndX.Should().BeApproximately(527.0, 3.0,
            "the pagenum's right edge should align with the body column right margin");

        bool hasLeader = chunks.Any(c => c.StartX > 200 && c.StartX < 500
                                         && c.Text.Contains('.'));
        hasLeader.Should().BeTrue("fresh dot leader should fill the gap between title and pagenum");
    }

    [FactIfSample(RamPdf)]
    public void ModifiedHeadingStaysAtSourcePosition()
    {
        // Node 215 ("3.4 Deposits and initial payments") sits below a table on page 5.
        // Pre-fix the modified heading drifted UP into the table area. The overflow
        // guard in EmitReplacement should keep the heading's baseline within a few
        // points of the source baseline.
        var pdfPath = TestSamples.Resolve(RamPdf);
        double sourceY;
        using (var srcReader = new PdfReader(pdfPath))
        using (var srcPdf = new PdfDocument(srcReader))
        {
            sourceY = FindBaselineY(srcPdf, 5, "3.4 Deposits");
        }

        var bytes = RunPipeline();
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        double editedY = FindBaselineY(pdf, 5, "3.4 Deposits");

        Math.Abs(editedY - sourceY).Should().BeLessThan(2.0,
            $"H3 baseline must stay near the source position (source={sourceY} edited={editedY})");
    }

    /// <summary>
    /// Return the baseline Y of the text chunk whose Y group's concatenated text starts
    /// with <paramref name="needle"/>. Some PDFs split a single visible line into many
    /// Tj/TJ operators — matching per-chunk would miss the phrase; grouping by baseline Y
    /// (bucketed at 1pt) reassembles the line first.
    /// </summary>
    private static double FindBaselineY(PdfDocument pdf, int page, string needle)
    {
        var byLine = new SortedDictionary<int, List<(double Y, double X, string Text)>>();
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (string.IsNullOrEmpty(s)) return;
            var start = tri.GetBaseline().GetStartPoint();
            double y = start.Get(1);
            double x = start.Get(0);
            int bucket = (int)Math.Round(y);
            if (!byLine.TryGetValue(bucket, out var list))
            {
                list = new List<(double, double, string)>();
                byLine[bucket] = list;
            }
            list.Add((y, x, s));
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(page));

        foreach (var list in byLine.Values)
        {
            list.Sort((a, b) => a.X.CompareTo(b.X));
            var joined = string.Concat(list.Select(t => t.Text));
            if (joined.Contains(needle, StringComparison.Ordinal))
            {
                return list[0].Y;
            }
        }
        throw new Xunit.Sdk.XunitException(
            "expected to find '" + needle + "' on page " + page +
            " but no baseline line contained it");
    }

    /// <summary>
    /// Return every text chunk (start-X, end-X, decoded text) on the baseline whose
    /// concatenated line contains <paramref name="needle"/>, sorted left-to-right.
    /// Used by <see cref="TocRowKeepsPagenumRightAnchored"/> to assert the geometry
    /// of the re-emitted TOC row.
    /// </summary>
    private readonly record struct RowChunk(double StartX, double EndX, string Text);

    private static IReadOnlyList<RowChunk> CollectRowChunks(PdfDocument pdf, int page, string needle)
    {
        var byLine = new SortedDictionary<int, List<RowChunk>>();
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (string.IsNullOrEmpty(s)) return;
            var start = tri.GetBaseline().GetStartPoint();
            var end = tri.GetBaseline().GetEndPoint();
            int bucket = (int)Math.Round(start.Get(1));
            if (!byLine.TryGetValue(bucket, out var list))
            {
                list = new List<RowChunk>();
                byLine[bucket] = list;
            }
            list.Add(new RowChunk(start.Get(0), end.Get(0), s));
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(page));

        foreach (var list in byLine.Values)
        {
            list.Sort((a, b) => a.StartX.CompareTo(b.StartX));
            var joined = string.Concat(list.Select(c => c.Text));
            if (joined.Contains(needle, StringComparison.Ordinal))
            {
                return list;
            }
        }
        return Array.Empty<RowChunk>();
    }

    /// <summary>
    /// Callback-based text-render listener (private copy — the sibling one in
    /// <see cref="EditEngineTests"/> is nested and not visible outside the class).
    /// </summary>
    private sealed class TextRenderListener : IEventListener
    {
        private static readonly ICollection<EventType> Supported = new HashSet<EventType> { EventType.RENDER_TEXT };
        private readonly Action<TextRenderInfo> _onText;
        public TextRenderListener(Action<TextRenderInfo> onText) { _onText = onText; }
        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            if (data is TextRenderInfo tri) _onText(tri);
        }
        public ICollection<EventType> GetSupportedEvents() => Supported;
    }
}
