using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Xunit;

namespace Apex.PdfEdit.Tests.Writer;

/// <summary>
/// Deleting a marked-content block must not move the text after it. Board Packet p3:
/// deleting "Resolution No. 4095" (MCID 13), whose block resets the text matrix with an
/// absolute <c>Tm</c> mid-paragraph, pulled the relatively-positioned ADJOURNMENT heading
/// up 4 lines while the rest of its paragraph — re-anchored by its own <c>Tm</c> — stayed.
/// </summary>
public sealed class ContentStreamMcidDeleterTests
{
    [Fact]
    public void DeletingBlockWithAbsoluteTmKeepsFollowingRelativeTextInPlace()
    {
        var source = BuildPdf();
        var before = BaselinesByText(source);

        var edited = Delete(source, mcid: 1);
        var after = BaselinesByText(edited);

        after.Should().NotContainKey("DELETED");
        after["FIRST"].Should().Be(before["FIRST"]);
        after["HEADING"].Should().Be(before["HEADING"],
            "a block placed by a relative TD after the deleted one must not move");
        after["TAIL"].Should().Be(before["TAIL"]);
    }

    [Fact]
    public void DeletingBlockWithQuoteOperatorKeepsItsLineAdvance()
    {
        // ' = T* + show: dropping it whole would lose the line move it carries.
        var source = BuildPdf(useQuote: true);
        var before = BaselinesByText(source);

        var after = BaselinesByText(Delete(source, mcid: 1));

        after["HEADING"].Should().Be(before["HEADING"]);
    }

    /// <summary>
    /// One BT: MCID 0 "FIRST" → MCID 1 "DELETED" (TD down, an absolute Tm, more text or a
    /// ' line) → MCID 2 "HEADING" (relative TD) → MCID 3 "TAIL" (own absolute Tm).
    /// </summary>
    private static byte[] BuildPdf(bool useQuote = false)
    {
        var buf = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(buf)))
        {
            doc.SetTagged();
            var page = doc.AddNewPage();
            var font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            var c = new PdfCanvas(page);
            c.BeginText().SetFontAndSize(font, 1).SetTextMatrix(12, 0, 0, 12, 72, 700);

            Mc(c, 0, () => c.ShowText("FIRST"));
            Mc(c, 1, () =>
            {
                c.GetContentStream().GetOutputStream().WriteString("0 -2.3 TD\n");
                c.ShowText("DELETED");
                if (useQuote)
                {
                    c.GetContentStream().GetOutputStream().WriteString("(DELETED) '\n");
                }
                else
                {
                    c.SetTextMatrix(12, 0, 0, 12, 72, 650);
                    c.GetContentStream().GetOutputStream().WriteString("3.8 -1.15 Td\n");
                    c.ShowText("DELETED");
                }
            });
            Mc(c, 2, () =>
            {
                c.GetContentStream().GetOutputStream().WriteString("0 -2.3 TD\n");
                c.ShowText("HEADING");
            });
            Mc(c, 3, () =>
            {
                c.SetTextMatrix(12, 0, 0, 12, 72, 500);
                c.ShowText("TAIL");
            });
            c.EndText();
        }
        return buf.ToArray();
    }

    private static void Mc(PdfCanvas c, int mcid, Action body)
    {
        var props = new PdfDictionary();
        props.Put(PdfName.MCID, new PdfNumber(mcid));
        c.BeginMarkedContent(PdfName.P, props);
        body();
        c.EndMarkedContent();
    }

    private static byte[] Delete(byte[] pdf, int mcid)
    {
        var buf = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(buf)))
        {
            ContentStreamMcidDeleter.Apply(doc.GetPage(1), new[] { new DeleteOverlay(1, mcid, "n" + mcid) });
        }
        return buf.ToArray();
    }

    /// <summary>First baseline start (x, y), rounded to 0.01pt, per distinct shown string.</summary>
    private static Dictionary<string, (double X, double Y)> BaselinesByText(byte[] pdf)
    {
        using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
        var result = new Dictionary<string, (double, double)>();
        var listener = new Listener(tri =>
        {
            var p = tri.GetBaseline().GetStartPoint();
            result.TryAdd(tri.GetText(), (Math.Round(p.Get(0), 2), Math.Round(p.Get(1), 2)));
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(1));
        return result;
    }

    private sealed class Listener : IEventListener
    {
        private readonly Action<TextRenderInfo> _onText;
        public Listener(Action<TextRenderInfo> onText) { _onText = onText; }
        public void EventOccurred(IEventData data, EventType type)
        {
            if (type == EventType.RENDER_TEXT && data is TextRenderInfo tri) _onText(tri);
        }
        public ICollection<EventType> GetSupportedEvents() => new HashSet<EventType> { EventType.RENDER_TEXT };
    }
}
