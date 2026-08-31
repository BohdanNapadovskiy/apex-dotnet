namespace Apex.PdfEdit.Tests;

/// <summary>
/// Resolves the output roots where tests persist rendered PDFs for manual inspection.
///
/// Two distinct roots so a reviewer's <c>POC_EDIT_DIR</c> stays clean:
/// <list type="bullet">
///   <item><see cref="ForSample"/> — the corpus-edit deliverable folder. One
///         <c>{sample}_edit.pdf</c> per sample from the corpus-wide sweep. Under
///         <c>POC_EDIT_DIR</c> (or in-repo <c>bin/edit/</c> when unset).</item>
///   <item><see cref="ForDiagnostic"/> — scratch folder for writer regression PDFs
///         (<c>_identity</c>, <c>_font-aware</c>, <c>_add</c>, <c>_delete</c>,
///         <c>_ocr-*</c>). Always under in-repo <c>bin/</c> (or
///         <c>POC_TEST_DIAG_DIR</c> when set) so they never pollute
///         <c>POC_EDIT_DIR</c>.</item>
/// </list>
/// </summary>
public static class TestOutputs
{
    private static string CorpusEditRoot()
    {
        var env = Environment.GetEnvironmentVariable("POC_EDIT_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine("bin", "edit");
    }

    private static string DiagnosticRoot()
    {
        var env = Environment.GetEnvironmentVariable("POC_TEST_DIAG_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return "bin";
    }

    /// <summary>
    /// Return (and create) the per-sample folder that receives the final
    /// <c>{sample}_edit.pdf</c> from the corpus-wide edit sweep. This is the
    /// user-facing deliverable — no other file should land here.
    /// </summary>
    public static string ForSample(string sample)
    {
        var dir = Path.Combine(CorpusEditRoot(), sample);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Return (and create) the per-sample scratch folder for writer-regression
    /// diagnostic PDFs. Kept separate from <see cref="ForSample"/> so the
    /// deliverable folder stays at one file per sample.
    /// </summary>
    public static string ForDiagnostic(string sample)
    {
        var dir = Path.Combine(DiagnosticRoot(), sample);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
