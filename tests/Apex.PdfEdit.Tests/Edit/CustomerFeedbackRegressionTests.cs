using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
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
