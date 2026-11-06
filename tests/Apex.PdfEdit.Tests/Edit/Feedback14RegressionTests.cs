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
/// Regression fixture for customer Feedback Ver 1.4 (2026-10-15): a <c>setText</c> on the
/// italic Note paragraph of Sample2 (node 8) with the full A–Z / a–z / punctuation set
/// rendered some characters upright.
///
/// The source's <c>BJSQKL+Arial,Italic</c> subset lacks most of the new glyphs. The
/// per-char fallback walked a pool that matched on <see cref="PageFontInventory.FamilyStem"/>
/// alone (",Italic" stripped) and so pulled them from the page's upright <c>Arial</c> /
/// <c>Arial,Bold</c> subsets, and the universal fallback loaded <c>arial.ttf</c> instead of
/// <c>ariali.ttf</c> for the same reason.
///
/// Runs the customer's own <c>edit.json</c>, document.json and geometry.json, stored in
/// the corpus as <c>Feedback-1.4-Sample2</c>.
/// </summary>
public sealed class Feedback14RegressionTests
{
    private const string Dir = "Feedback-1.4-Sample2";
    private const string Pdf = Dir + "/" + Dir + ".pdf";
    private const string Doc = Dir + "/" + Dir + "-document.json";
    private const string Geom = Dir + "/" + Dir + "-geometry.json";
    private const string Edits = Dir + "/" + Dir + "-edits.json";

    private readonly record struct Glyph(string Text, string Font);

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
            result.Issues.Should().BeEmpty("both Feedback 1.4 setTexts must apply cleanly");
            result.AppliedOpIds.Should().HaveCount(edits.Operations.Count);
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }
        return outBuf.ToArray();
    }

    /// <summary>Page-1 glyphs in emit order, each with its font's PostScript name.</summary>
    private static List<Glyph> PageOneGlyphs(byte[] bytes)
    {
        using var reader = new PdfReader(new MemoryStream(bytes));
        using var pdf = new PdfDocument(reader);
        var glyphs = new List<Glyph>();
        var listener = new TextRenderListener(tri =>
        {
            foreach (var ch in tri.GetCharacterRenderInfos())
            {
                var name = ch.GetFont()?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? string.Empty;
                glyphs.Add(new Glyph(ch.GetText(), name));
            }
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(1));
        return glyphs;
    }

    /// <summary>The edited Note paragraph's glyphs: from "*Although" up to the next heading.</summary>
    private static List<Glyph> NoteGlyphs(List<Glyph> glyphs)
    {
        var text = string.Concat(glyphs.Select(g => g.Text));
        int start = text.IndexOf("*Although", StringComparison.Ordinal);
        int end = text.IndexOf("Learning is better", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the Note paragraph must render");
        end.Should().BeGreaterThan(start);

        // Glyph texts are single chars here (no ligatures in Arial), so text offsets map 1:1.
        glyphs.Should().OnlyContain(g => g.Text.Length <= 1);
        return glyphs.Skip(start).Take(end - start).Where(g => !string.IsNullOrWhiteSpace(g.Text)).ToList();
    }

    [FactIfSample(Pdf)]
    public void EditedNoteParagraphRendersEveryGlyphInItalic()
    {
        var note = NoteGlyphs(PageOneGlyphs(RunPipeline()));
        string.Concat(note.Select(g => g.Text)).Should().Contain("QWERTYUIOPASDFGHJKLZXCVBNM")
            .And.Contain("!@#$%^&*()_+{}:?><,./[]=-");

        var upright = note.Where(g => !PageFontInventory.IsItalicName(g.Font)).ToList();
        upright.Should().BeEmpty("the source paragraph is all Arial,Italic; got upright glyphs {0}",
            string.Join(" ", upright.Select(g => $"'{g.Text}'={g.Font}")));
    }

    [FactIfSample(Pdf)]
    public void EditedNoteParagraphNeverBorrowsBoldGlyphs()
    {
        // Before the face-aware pick, 'L' / 'B' / '-' came from Arial,BoldItalic once the
        // italic filter was in place — weight must hold too when the system font has them.
        var note = NoteGlyphs(PageOneGlyphs(RunPipeline()));
        var bold = note.Where(g => g.Font.Contains("bold", StringComparison.OrdinalIgnoreCase)).ToList();
        bold.Should().BeEmpty("the source paragraph is regular weight; got bold glyphs {0}",
            string.Join(" ", bold.Select(g => $"'{g.Text}'={g.Font}")));
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
