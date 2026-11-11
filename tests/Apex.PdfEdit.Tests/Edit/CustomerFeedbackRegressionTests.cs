using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Regression fixtures for the customer feedback received 2026-09-04:
/// <list type="bullet">
///   <item>Postman POST /edit against the 1TCC-MS4-Staff-Handbook sample.</item>
///   <item><c>addListItem</c> with a body containing full ASCII + symbols silently
///       dropped characters not present in the source PDF's embedded font subset
///       (e.g. uppercase X/Y/J/Z, brackets, ampersand-family) — the API returned
///       200 with no error and the rendered PDF was missing those glyphs.</item>
///   <item><c>setText</c> with the same broad string threw the actionable
///       "Restrict the edit to characters..." error — the fail-loud path already
///       existed on that op.</item>
/// </list>
///
/// P2 (2026-09-04, follow-on): the writer now runs the customer's exact text
/// through a per-char font picker (<see cref="MultiFontLineEmitter"/>) — chars
/// covered by the source subset render there; the rest fall through to an
/// embedded system fallback (Arial-family). The pre-flight only rejects when
/// even the fallback can't render a code point (astral emoji, etc.). So the
/// customer's original addListItem now:
/// <list type="bullet">
///   <item>Applies cleanly (AppliedOpIds contains the op id).</item>
///   <item>Produces an AddListItemOverlay in the plan.</item>
///   <item>Yields no EditIssue (a server-side log line records the mixed face).</item>
/// </list>
///
/// These tests intentionally use the customer's exact fixture — parent id="164"
/// (L container whose LIs render on page 5), inheritFrom="176" (LBody mcid=14),
/// bodyText verbatim — to prevent P2 from regressing under refactors.
/// </summary>
public sealed class CustomerFeedbackRegressionTests
{
    private const string HandbookDir = "1TCC-MS4-Staff-Handbook-2024-03-14-Remediated";
    private const string HandbookDoc = HandbookDir + "/" + HandbookDir + "-document.json";
    private const string HandbookGeom = HandbookDir + "/" + HandbookDir + "-geometry.json";
    private const string HandbookPdf = HandbookDir + "/" + HandbookDir + ".pdf";

    /// <summary>
    /// Customer's exact bodyText from the Feedback.docx screenshot, pasted verbatim
    /// as a single string literal so a reviewer can compare byte-for-byte against
    /// the customer report without decoding concatenation.
    /// </summary>
    private const string CustomerBodyText =
        "APEX inserted list item: QWERTYUIOPASDFGHJKLZXCVBNM qwertyuiopasdfghjklzxcvbnm []{}!@#$%^&*()_+-=<>?:''1234567890.,/|";

    [FactIfSample(HandbookPdf)]
    public void CustomerAddListItemBodyIsAppliedViaFallback()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(HandbookDoc));
        var edits = new EditsJson
        {
            SchemaVersion = "1",
            BaseDocument = Path.GetFileName(HandbookDoc),
            Operations =
            {
                // "\uf0d8": Wingdings arrow head bullet — matches the source LIs on page 5.
                new AddListItemOp("tcc-p5-addLi", 5, "164", -1,
                    "\uf0d8", CustomerBodyText, new StyleSpec("176"))
            }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(HandbookPdf));
        var result = new EditEngine(resolver).Apply(doc, edits);

        result.Issues.Should().BeEmpty(
            "P2: the embedded system fallback covers ASCII+symbols, so the op should proceed without EditIssue");
        result.AppliedOpIds.Should().Equal("tcc-p5-addLi");
        result.Plan.AddListItemOverlays.Should().HaveCount(1);
        var overlay = result.Plan.AddListItemOverlays[0];
        overlay.BodyText.Should().Be(CustomerBodyText, "the writer receives the full string verbatim");
    }

    /// <summary>
    /// Golden path: same op shape, body pared down to characters that ARE in the
    /// donor mcid=14 subset (LBody 176 renders sentences using lowercase Latin,
    /// spaces, and period). Proves the pre-flight isn't over-broad and single-font
    /// emit still happens when the source covers everything.
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void CustomerAddListItemBodyWithInSubsetCharsIsApplied()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(HandbookDoc));
        var edits = new EditsJson
        {
            Operations =
            {
                new AddListItemOp("tcc-p5-addLi-ok", 5, "164", -1,
                    "\uf0d8",
                    "APE inserted list item within source subset chars only.",
                    new StyleSpec("176"))
            }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(HandbookPdf));
        var result = new EditEngine(resolver).Apply(doc, edits);

        result.Issues.Should().BeEmpty();
        result.AppliedOpIds.Should().Equal("tcc-p5-addLi-ok");
        result.Plan.AddListItemOverlays.Should().HaveCount(1);
    }

    /// <summary>
    /// Round-trip the same DTO shape EditService returns to /edit callers and assert
    /// the serialized shape is what Postman would see. Guards against contract drift
    /// on the appliedOps + issues arrays.
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void CustomerScenarioSerializesToAppliedOpsWithNoIssues()
    {
        var doc = DocumentJsonLoader.Load(TestSamples.Resolve(HandbookDoc));
        var edits = new EditsJson
        {
            Operations =
            {
                new AddListItemOp("tcc-p5-addLi", 5, "164", -1,
                    "\uf0d8", CustomerBodyText, new StyleSpec("176"))
            }
        };

        using var resolver = new SourcePdfFontResolver(TestSamples.Resolve(HandbookPdf));
        var result = new EditEngine(resolver).Apply(doc, edits);

        var payload = new
        {
            appliedOps = result.AppliedOpIds,
            issues = result.Issues.Select(i => new
            {
                opId = i.OpId,
                type = i.OpType,
                message = i.Message,
            }).ToList(),
        };
        var opts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.Serialize(payload, opts);

        json.Should().Contain("\"appliedOps\":[\"tcc-p5-addLi\"]");
        json.Should().Contain("\"issues\":[]");
    }

    /// <summary>
    /// End-to-end verification: run engine + <see cref="SourceBasedWriter"/> against
    /// the customer's exact op and extract text from page 5 of the output PDF.
    /// Assert every non-whitespace codepoint from the input bodyText appears in the
    /// extracted text — i.e., no silent drop. Writes the artifact under
    /// <see cref="TestOutputs.ForDiagnostic"/> so a reviewer can eyeball the mixed-face
    /// rendering.
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void CustomerAddListItemBodyRoundTripsAllChars()
    {
        var pdfPath = TestSamples.Resolve(HandbookPdf);
        var docPath = TestSamples.Resolve(HandbookDoc);
        var doc = DocumentJsonLoader.Load(docPath);
        var edits = new EditsJson
        {
            Operations =
            {
                new AddListItemOp("tcc-p5-addLi", 5, "164", -1,
                    "\uf0d8", CustomerBodyText, new StyleSpec("176"))
            }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.AppliedOpIds.Should().Equal("tcc-p5-addLi");
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForDiagnostic(HandbookDir),
            HandbookDir + "_customer_addLi_edit.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page5Text = PdfTextExtractor.GetTextFromPage(pdf.GetPage(5));

        // Every unique non-whitespace codepoint in the input must survive.
        var missing = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < CustomerBodyText.Length;)
        {
            int cp = char.ConvertToUtf32(CustomerBodyText, i);
            i += char.IsHighSurrogate(CustomerBodyText[i]) ? 2 : 1;
            if (cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n') continue;
            if (!seen.Add(cp)) continue;
            var s = char.ConvertFromUtf32(cp);
            if (!page5Text.Contains(s, StringComparison.Ordinal))
            {
                missing.Add(cp);
            }
        }
        missing.Should().BeEmpty(
            "P2 must render every char (via fallback if source subset lacks it); missing code points: " +
            string.Join(", ", missing.Select(m => $"U+{m:X4}")));
    }

    /// <summary>
    /// Sibling to <see cref="CustomerAddListItemBodyRoundTripsAllChars"/> for setText:
    /// the same broad ASCII+symbols string is written into an existing MCID via
    /// <see cref="ContentStreamMcidReplacer"/> and the extracted text is verified for
    /// coverage. Exercises the setText writer's per-char font-split path (2026-09-04
    /// P2 follow-on).
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void CustomerSetTextBroadStringRoundTripsAllChars()
    {
        var pdfPath = TestSamples.Resolve(HandbookPdf);
        var docPath = TestSamples.Resolve(HandbookDoc);
        var doc = DocumentJsonLoader.Load(docPath);
        // id=167 is the LBody at page 5 mcid=8, prose text — swap in a broad string.
        const string BroadText =
            "APEX edited: broad set QWERTYUIOPASDFGHJKLZXCVBNM " +
            "qwertyuiopasdfghjklzxcvbnm []{}!@#$%^&*()_+-=<>?:.,/|1234567890";
        var edits = new EditsJson
        {
            Operations = { SetTextOp.Of("tcc-p5-setText", "167", BroadText) }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty();
            result.AppliedOpIds.Should().Equal(new[] { "tcc-p5-setText" });
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForDiagnostic(HandbookDir),
            HandbookDir + "_customer_setText_edit.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page5Text = PdfTextExtractor.GetTextFromPage(pdf.GetPage(5));

        var missing = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < BroadText.Length;)
        {
            int cp = char.ConvertToUtf32(BroadText, i);
            i += char.IsHighSurrogate(BroadText[i]) ? 2 : 1;
            if (cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n') continue;
            if (!seen.Add(cp)) continue;
            var s = char.ConvertFromUtf32(cp);
            if (!page5Text.Contains(s, StringComparison.Ordinal))
            {
                missing.Add(cp);
            }
        }
        missing.Should().BeEmpty(
            "P2 setText must render every char; missing code points: " +
            string.Join(", ", missing.Select(m => $"U+{m:X4}")));
    }

    /// <summary>
    /// The customer's Postman payload, verbatim, inline in the test method so a
    /// reviewer can see the exact string that P1 rejected and P2 accepts — no
    /// const indirection, no concatenation, no escape sequences. This is the
    /// same request body that /edit customer support debugged from Feedback.docx
    /// (2026-09-04):
    ///
    /// <code>
    /// {
    ///   "id": "tcc-p5-addLi",
    ///   "type": "addListItem",
    ///   "page": 5,
    ///   "parent": "164",
    ///   "index": -1,
    ///   "labelText": "\uf0d8",
    ///   "bodyText": "APEX inserted list item: QWERTYUIOPASDFGHJKLZXCVBNM qwertyuiopasdfghjklzxcvbnm []{}!@#$%^&amp;*()_+-=&lt;&gt;?:''1234567890.,/|",
    ///   "style": { "inheritFrom": "176" }
    /// }
    /// </code>
    ///
    /// The assertions verify BOTH engine surface (op applied, no EditIssue) AND
    /// writer surface (every non-whitespace codepoint from the raw string appears
    /// in the extracted page-5 text). If either regresses, this test breaks
    /// loudly and points straight at the customer report.
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void CustomerRawPostmanBodyTextRendersEndToEnd()
    {
        var pdfPath = TestSamples.Resolve(HandbookPdf);
        var docPath = TestSamples.Resolve(HandbookDoc);
        var doc = DocumentJsonLoader.Load(docPath);

        // ↓↓↓ RAW customer payload — do not touch, do not refactor, do not concatenate.
        var op = new AddListItemOp(
            Id:        "tcc-p5-addLi",
            Page:      5,
            Parent:    "164",
            Index:     -1,
            LabelText: "\uf0d8",
            BodyText:  "APEX inserted list item: QWERTYUIOPASDFGHJKLZXCVBNM qwertyuiopasdfghjklzxcvbnm []{}!@#$%^&*()_+-=<>?:''1234567890.,/|",
            Style:     new StyleSpec("176"));
        // ↑↑↑ RAW customer payload.

        var edits = new EditsJson
        {
            SchemaVersion = "1",
            BaseDocument = Path.GetFileName(HandbookDoc),
            Operations = { op }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, edits);
            result.Issues.Should().BeEmpty("customer payload must apply cleanly under P2");
            result.AppliedOpIds.Should().Equal(new[] { "tcc-p5-addLi" });
            result.Plan.AddListItemOverlays.Should().HaveCount(1);
            result.Plan.AddListItemOverlays[0].BodyText.Should().Be(op.BodyText,
                "the overlay must carry the raw bodyText byte-for-byte to the writer");
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        // Persist the artifact under the same POC_EDIT_DIR layout the other E2E tests use,
        // and also mirror it into C:/projects/pdf/apex/edit/p2_raw/ (the folder the user
        // requested for customer-facing review artifacts).
        var debug = Path.Combine(TestOutputs.ForDiagnostic(HandbookDir),
            HandbookDir + "_customer_raw_addLi_edit.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());
        TryMirrorToCustomerReviewFolder(outBuf.ToArray(), HandbookDir + "_customer_raw_addLi_edit.pdf");

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);
        var page5Text = PdfTextExtractor.GetTextFromPage(pdf.GetPage(5));

        var missing = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < op.BodyText.Length;)
        {
            int cp = char.ConvertToUtf32(op.BodyText, i);
            i += char.IsHighSurrogate(op.BodyText[i]) ? 2 : 1;
            if (cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n') continue;
            if (!seen.Add(cp)) continue;
            if (!page5Text.Contains(char.ConvertFromUtf32(cp), StringComparison.Ordinal))
            {
                missing.Add(cp);
            }
        }
        missing.Should().BeEmpty(
            "P2 must render every char in the customer's raw Postman payload; missing: " +
            string.Join(", ", missing.Select(m => $"U+{m:X4} '{char.ConvertFromUtf32(m)}'")));

        // Spot-check the exact chars the customer's screenshot showed as dropped: X, Y, J, Z
        // (upper-Latin subset gaps), plus bracket/brace/pipe/at-sign — these were the
        // "APE inserted..." truncation signatures. Under P2 they all survive.
        foreach (var glyph in new[] { "X", "Y", "J", "Z", "[", "]", "{", "}", "@", "|" })
        {
            page5Text.Should().Contain(glyph,
                $"customer's screenshot showed '{glyph}' silently dropped pre-P2");
        }
    }

    /// <summary>
    /// Feedback 1.1 (2026-09-23) — customer POST /edit against the TCC handbook flagged
    /// two writer regressions on paragraphs whose replacement wraps to more than one line:
    /// <list type="number">
    ///   <item><b>Page 3 (node 135)</b> — a doubled-content edit collapsed onto a single
    ///       line that ran under the adjacent river photo, instead of preserving the
    ///       source paragraph's line breaks. Root cause: the overflow guard capped
    ///       <c>maxLinesThatFit</c> using the source bbox height even when NO next
    ///       sibling existed on the page (the paragraph was at the page bottom), so a
    ///       longer edit got squashed to one line rather than allowed to grow downward.</item>
    ///   <item><b>Page 4 (node 139)</b> — a long-string edit rendered two wrapped lines
    ///       on top of each other, only 2.22pt apart. Root cause: <c>SourceLineGap</c>
    ///       clustered glyph Ys using a 1pt tolerance, so bold-italic "illicit discharge"
    ///       (Y=517.3 with a taller bbox) read as a separate baseline from the surrounding
    ///       regular text (Y=519.5) and the writer treated 2.22pt as the block's leading.</item>
    /// </list>
    /// Both are guarded here: the test drives the customer's exact edits.json against
    /// the TCC handbook and asserts (a) the wrapped edit on page 3 emits multiple
    /// distinct baselines with body-leading spacing, and (b) the wrapped edit on page 4
    /// emits baselines separated by at least a full line-height (~12pt+), never <5pt.
    /// </summary>
    [FactIfSample(HandbookPdf)]
    public void FeedbackV11WrappedEditsUseFullLeadingNotBboxDrift()
    {
        var pdfPath = TestSamples.Resolve(HandbookPdf);
        var docPath = TestSamples.Resolve(HandbookDoc);
        var geomPath = TestSamples.Resolve(HandbookGeom);
        var doc = DocumentJsonLoader.Load(docPath);
        var geom = GeometryJsonLoader.Load(geomPath);

        // Customer's exact edits.json from Feedback Ver 1.1/SampleFiles.zip — the two
        // setTexts that reproduced the reported issues. Kept inline verbatim.
        var edits = new EditsJson
        {
            SchemaVersion = "1",
            BaseDocument = Path.GetFileName(HandbookDoc),
            Operations =
            {
                SetTextOp.Of("tcc-p2-setText", "135",
                    "This Handbook shall serve as the guiding document for TCC staff engaged in any activity on TCC campuses that could potentially impact water quality. This Handbook shall serve as the guiding document for TCC staff engaged in any activity on TCC campuses that could potentially impact water quality."),
                SetTextOp.Of("tcc-p4-setText", "139",
                    "edit: the text QWERTYUIOPASDFGHJKLZXCVBNM[]{}1234567890 :"),
            }
        };

        var outBuf = new MemoryStream();
        using (var resolver = new SourcePdfFontResolver(pdfPath))
        {
            var result = new EditEngine(resolver).Apply(doc, geom, edits);
            result.Issues.Should().BeEmpty();
            result.AppliedOpIds.Should().BeEquivalentTo(new[] { "tcc-p2-setText", "tcc-p4-setText" });
            new SourceBasedWriter(pdfPath).Write(result.Plan, outBuf);
        }

        var debug = Path.Combine(TestOutputs.ForDiagnostic(HandbookDir),
            HandbookDir + "_feedback_v11_edit.pdf");
        File.WriteAllBytes(debug, outBuf.ToArray());

        using var reader = new PdfReader(new MemoryStream(outBuf.ToArray()));
        using var pdf = new PdfDocument(reader);

        // --- Page 3, node 135: wrapped-line preservation ---
        // Every emitted baseline that carries part of "This Handbook shall serve as the
        // guiding document". Pre-fix there was only ONE such baseline (all text collapsed
        // to a single line). Post-fix the doubled sentence wraps to 3+ lines.
        var handbookBaselines = CollectBaselinesContaining(pdf, page: 3, phrase: "guiding document");
        handbookBaselines.Should().HaveCountGreaterThan(1,
            "Feedback 1.1 page 3: doubled paragraph must wrap to multiple lines, not collapse under the image");

        // Distinct-baseline gap between wrapped lines must be a real body leading
        // (~20pt at 12pt body). Pre-fix collapse produced ONE baseline; if a future
        // change re-introduces a small-gap stack, this catches it too.
        handbookBaselines.Sort();
        for (int i = 1; i < handbookBaselines.Count; i++)
        {
            double gap = handbookBaselines[i] - handbookBaselines[i - 1];
            gap.Should().BeGreaterThan(10.0,
                $"page 3 wrapped baselines must be at least a full line-height apart (gap {gap:F2}pt at Y {handbookBaselines[i]:F2})");
        }

        // --- Page 4, node 139: no 2.22pt intra-line stack ---
        // Baselines carrying the customer's replacement. Pre-fix, "edit: the text" and
        // "QWERTYUIOP..." landed on baselines 2.22pt apart (SourceLineGap treated the
        // bold-italic run's bigger bbox as a distinct line). Post-fix wrapped lines
        // sit at least one full font-height apart.
        var illicitBaselines = CollectBaselinesContaining(pdf, page: 4, phrase: "QWERT");
        illicitBaselines.Should().NotBeEmpty("page 4 replacement text must render");
        var editBaselines = CollectBaselinesContaining(pdf, page: 4, phrase: "edit: the");
        editBaselines.Should().NotBeEmpty("page 4 replacement text must render");

        // The "edit: the text" baseline and the "QWERT..." baseline are either the SAME
        // baseline (single-line rendering) OR separated by a full leading. A tiny 2-3pt
        // separation is the exact regression the fix targets.
        foreach (var eb in editBaselines)
        {
            foreach (var qb in illicitBaselines)
            {
                if (Math.Abs(eb - qb) < 0.1) continue;   // same baseline — fine
                Math.Abs(eb - qb).Should().BeGreaterThan(6.0,
                    $"page 4 wrapped lines must not stack at bbox-descender drift " +
                    $"(edit-baseline {eb:F2}, QWERT-baseline {qb:F2}, gap {Math.Abs(eb - qb):F2}pt — pre-fix was 2.22pt)");
            }
        }
    }

    /// <summary>
    /// Return the sorted distinct baseline Ys of any text chunks that contain
    /// <paramref name="phrase"/> as an infix. Baselines are bucketed at 0.5pt to
    /// coalesce iText's per-glyph baseline reports back into visual lines.
    /// </summary>
    private static List<double> CollectBaselinesContaining(PdfDocument pdf, int page, string phrase)
    {
        var chunksByY = new SortedDictionary<int, List<(double X, double Y, string Text)>>();
        var listener = new PhraseBaselineListener((tri, y, x, text) =>
        {
            int bucket = (int)Math.Round(y * 2);   // 0.5pt buckets
            if (!chunksByY.TryGetValue(bucket, out var list))
            {
                list = new List<(double, double, string)>();
                chunksByY[bucket] = list;
            }
            list.Add((x, y, text));
        });
        new PdfCanvasProcessor(listener).ProcessPageContent(pdf.GetPage(page));

        var hits = new List<double>();
        foreach (var list in chunksByY.Values)
        {
            list.Sort((a, b) => a.X.CompareTo(b.X));
            var joined = string.Concat(list.Select(t => t.Text));
            if (joined.Contains(phrase, StringComparison.Ordinal))
            {
                hits.Add(list[0].Y);
            }
        }
        return hits;
    }

    private sealed class PhraseBaselineListener : IEventListener
    {
        private static readonly ICollection<EventType> Supported = new HashSet<EventType> { EventType.RENDER_TEXT };
        private readonly Action<TextRenderInfo, double, double, string> _onText;
        public PhraseBaselineListener(Action<TextRenderInfo, double, double, string> onText) { _onText = onText; }
        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT || data is not TextRenderInfo tri) return;
            var s = tri.GetText();
            if (string.IsNullOrEmpty(s)) return;
            var start = tri.GetBaseline().GetStartPoint();
            _onText(tri, start.Get(1), start.Get(0), s);
        }
        public ICollection<EventType> GetSupportedEvents() => Supported;
    }

    /// <summary>
    /// Best-effort mirror of a rendered fixture PDF into the customer-review folder the
    /// user asked for (2026-09-04). Silent on absence of the target folder so the test
    /// remains portable across dev machines that don't have that path.
    /// </summary>
    private static void TryMirrorToCustomerReviewFolder(byte[] pdfBytes, string filename)
    {
        try
        {
            const string ReviewRoot = @"C:\projects\pdf\apex\edit\p2_raw";
            Directory.CreateDirectory(ReviewRoot);
            File.WriteAllBytes(Path.Combine(ReviewRoot, filename), pdfBytes);
        }
        catch
        {
            // Not fatal — the primary artifact still lands under TestOutputs.ForDiagnostic.
        }
    }
}
