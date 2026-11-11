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
/// Regression fixture for customer Feedback Ver 1.3 (2026-10-11): after a <c>setText</c>
/// on the justified italic Note paragraph of Sample2 (node 8) through <c>/edit</c>, word
/// spacing no longer matched the original.
///
/// The source squeezes its spaces (2.13pt against Arial's 2.50pt natural width) to keep
/// "University" at the end of line 2. Re-wrapping at natural spacing dropped the word to
/// line 3 and stretched the line it left to 4.87pt gaps. Fixed by
/// <see cref="EditEngine"/> <c>SourceMinWordGap</c> +
/// <see cref="ContentStreamMcidReplacer"/> <c>SpaceShrinkPt</c>.
///
/// Runs the customer's own <c>edit.json</c>, document.json and geometry.json, stored in
/// the corpus as <c>Feedback-1.3-Sample2</c>.
/// </summary>
public sealed class Feedback13RegressionTests
{
    private const string Dir = "Feedback-1.3-Sample2";
    private const string Pdf = Dir + "/" + Dir + ".pdf";
    private const string Doc = Dir + "/" + Dir + "-document.json";
    private const string Geom = Dir + "/" + Dir + "-geometry.json";
    private const string Edits = Dir + "/" + Dir + "-edits.json";

    private readonly record struct Line(double Y, double StartX, double EndX, string Text);

    private static byte[] RunPipeline()
    {
        var pdfPath = TestSamples.Resolve(Pdf);
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(Doc));
        var geom = GeometryJsonLoader.Load(TestSamples.Resolve(Geom));
        var edits = EditsJsonLoader.Load(TestSamples.Resolve(Edits));
        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, geom, edits);
            result.Issues.Should().BeEmpty("both Feedback 1.3 setTexts must apply cleanly");
            result.AppliedOpIds.Should().HaveCount(edits.Operations.Count);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }
        return outBuf.ToArray();
    }

    /// <summary>Page-1 text grouped by baseline, top to bottom, in emit order.</summary>
    private static List<Line> PageOneLines(byte[] bytes)
    {
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        var byY = new Dictionary<double, Line>();
        var listener = new TextRenderListener(tri =>
        {
            var s = tri.GetText();
            if (string.IsNullOrEmpty(s)) return;
            var baseline = tri.GetBaseline();
            double y = Math.Round(baseline.GetStartPoint().Get(1), 1);
            double x0 = baseline.GetStartPoint().Get(0);
            // Trailing spaces carry advance but no ink — keep them out of the line end.
            double x1 = string.IsNullOrWhiteSpace(s) ? double.NegativeInfinity : baseline.GetEndPoint().Get(0);
            byY[y] = byY.TryGetValue(y, out var cur)
                ? new Line(y, Math.Min(cur.StartX, x0), Math.Max(cur.EndX, x1), cur.Text + s)
                : new Line(y, x0, x1, s);
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(1));
        return byY.Values.OrderByDescending(l => l.Y).ToList();
    }

    [FactIfSample(Pdf)]
    public void NoteParagraphKeepsSourceLineBreakAfterEdit()
    {
        var lines = PageOneLines(RunPipeline());
        int second = lines.FindIndex(l => l.Text.Contains("change without notice"));
        second.Should().BeGreaterThan(0, "the edited Note paragraph must render its second line");

        lines[second].Text.TrimEnd().Should().EndWith("Affiliated University",
            "the source fits 'University' on line 2 by compressing spaces; the edit must too");
        lines[second + 1].Text.TrimStart().Should().StartWith("Colleges.",
            "line 3 starts where the source's does");
        lines[second + 1].Text.Should().Contain("Added extra text content.");
    }

    [FactIfSample(Pdf)]
    public void NoteParagraphSecondLineStaysInsideBboxWithSourceLikeGaps()
    {
        var lines = PageOneLines(RunPipeline());
        var second = lines.First(l => l.Text.Contains("change without notice"));

        // Node 8 bbox: x=84.98, width=441.67 → right edge 526.65.
        second.StartX.Should().BeApproximately(84.98, 0.5);
        second.EndX.Should().BeApproximately(526.65, 1.0,
            "a compressed line must still end on the justified right edge, not overflow it");

        // Mean word gap = (line width − ink width at natural spacing) / gaps + natural
        // space. Source: 2.13pt. Pre-fix output: 4.87pt.
        string text = second.Text.Trim();
        int gaps = text.Count(c => c == ' ');
        gaps.Should().Be(16);
        const double naturalSpace = 2.502;      // Arial space at 9pt
        const double naturalWidth = 448.2;      // line 2 incl. 'University' at natural spacing
        double meanGap = naturalSpace + ((second.EndX - second.StartX) - naturalWidth) / gaps;
        meanGap.Should().BeInRange(1.9, 2.4, "source line 2 has 2.13pt gaps");
    }

    [FactIfSample(Pdf)]
    public void PreambleParagraphEditStillWrapsAtNaturalSpacing()
    {
        // Node 5's source never compresses (tightest line 4.34pt), so its wrap must be
        // unaffected by the Feedback 1.3 change.
        var lines = PageOneLines(RunPipeline());
        var first = lines.First(l => l.Text.Contains("This document outlines"));
        first.Text.TrimEnd().Should().EndWith("residence contract with");
        first.EndX.Should().BeApproximately(526.62, 1.0);
        lines.Should().Contain(l => l.Text.TrimStart().StartsWith("Campus Housing for 2025-2026")
            && l.Text.Contains("Added extra content text."));
    }

    /// <summary>
    /// Callback-based text-render listener (private copy — the sibling ones are nested
    /// in other test classes and not visible here).
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
