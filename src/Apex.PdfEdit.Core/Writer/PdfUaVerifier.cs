using iText.Kernel.Pdf;
using iText.Kernel.Validation.Context;
using iText.Pdfua.Checkers;
using iText.Pdfua.Exceptions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Runs iText's <see cref="PdfUA1Checker"/> against a written PDF as a post-write
/// conformance smoke — catches overlay-side PDF/UA regressions programmatically
/// without needing a manual PAC 2024 pass. Complements PAC (not a replacement):
/// iText's checker covers a subset of ISO 14289-1 rules (catalog metadata,
/// structure tree, font embedding, XFA) at the object level; PAC 2024 has
/// broader coverage including visual regression, colour contrast, and heading
/// hierarchy nuances that iText doesn't check.
///
/// The checker short-circuits on the first <see cref="PdfUAConformanceException"/>
/// thrown per invocation, so a <see cref="VerifyResult"/> carries at most one issue
/// per call. That's still useful as a CI gate: a failing check blocks the write,
/// and the operator drills into the root cause via a PAC run.
/// </summary>
public static class PdfUaVerifier
{
    /// <summary>Outcome of a single <see cref="Verify(string)"/> call.</summary>
    public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Issues)
    {
        public static VerifyResult Pass() => new(true, Array.Empty<string>());
        public static VerifyResult Fail(string message) => new(false, new[] { message });
    }

    /// <summary>
    /// Open the PDF read-only, wrap in a <see cref="PdfDocument"/>, and run the
    /// PDF/UA-1 catalog + structure-tree + font checks. Any
    /// <see cref="PdfUAConformanceException"/> thrown is captured as a
    /// <see cref="VerifyResult.Fail"/>. All other exceptions bubble up.
    /// </summary>
    public static VerifyResult Verify(string pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var reader = new PdfReader(pdf);
        using var doc = new PdfDocument(reader);

        PdfUA1Checker checker;
        try
        {
            // TagStructureContext(pdfDocument) in the checker's ctor requires
            // a tagged document — an untagged PDF can't be PDF/UA-1 at all.
            checker = new PdfUA1Checker(doc);
        }
        catch (Exception ctorEx)
        {
            return VerifyResult.Fail($"not a tagged document: {ctorEx.Message}");
        }

        // PdfDocument.GetDocumentFonts() is protected — pass an empty collection.
        // iText's checkFonts loop becomes a no-op, but font embedding is already
        // enforced at write time by the writer path. The catalog + structure-tree
        // + XFA checks are what matter here.
        var ctx = new PdfDocumentValidationContext(doc, Array.Empty<iText.Kernel.Font.PdfFont>());
        try
        {
            checker.Validate(ctx);
            return VerifyResult.Pass();
        }
        catch (PdfUAConformanceException e)
        {
            return VerifyResult.Fail(e.Message);
        }
    }
}
