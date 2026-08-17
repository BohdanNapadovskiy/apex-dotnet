using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Populates the <c>/TU</c> (tooltip / alternative-name) entry on form-field
/// <c>/Widget</c> annotations that lack it. PDF/UA-1 §7.18.5 requires every form
/// field to expose an accessible name via either <c>/TU</c> on the widget or an
/// alternative-description on its enclosing <c>Form</c> StructElem — most real-world
/// source PDFs carry neither, and stamp-mode identity propagates the gap verbatim.
///
/// Derivation rules (first non-empty wins):
/// <list type="number">
///   <item>Existing <c>/TU</c> — respected; never overwritten.</item>
///   <item>Fully-qualified field name — walk the field's <c>/Parent</c> chain
///       collecting <c>/T</c> partial names, join with <c>.</c>
///       (e.g. <c>Address.Street</c>). Matches PDF §12.7.3.2 field-naming.</item>
///   <item>Fallback: <c>"Form field on page N"</c> when no <c>/T</c> is present
///       anywhere in the parent chain.</item>
/// </list>
///
/// The pass is idempotent. Non-Widget annotations are skipped.
///
/// This is a .NET-only enhancement — no Java equivalent exists. Java's stamp-mode
/// currently emits the same PDF/UA-1 form-field failure this class fixes.
/// </summary>
public static class FormFieldTuFiller
{
    /// <summary>
    /// Populate missing <c>/TU</c> on every Widget annotation in the document.
    /// Returns the count of annotations patched.
    /// </summary>
    public static int ApplyAll(PdfDocument doc, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var log = logger ?? NullLogger.Instance;
        int total = 0;
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var page = doc.GetPage(p);
            var annots = page.GetPdfObject().GetAsArray(PdfName.Annots);
            if (annots is null) continue;
            for (int i = 0; i < annots.Size(); i++)
            {
                var annot = annots.GetAsDictionary(i);
                if (annot is null) continue;
                if (PopulateIfMissing(annot, p)) total++;
            }
        }
        if (total > 0)
        {
            log.LogInformation("[form-field-tu] patched {N} Widget annotation(s) with derived /TU", total);
        }
        return total;
    }

    /// <summary>
    /// Patch a single annotation dict in place. Returns true when <c>/TU</c> was
    /// newly set. Safe to call on non-Widget dicts (returns false).
    /// </summary>
    public static bool PopulateIfMissing(PdfDictionary? annot, int pageNumber)
    {
        if (annot is null) return false;
        if (!PdfName.Widget.Equals(annot.GetAsName(PdfName.Subtype))) return false;
        // Respect existing /TU — don't overwrite even if empty.
        if (annot.Get(PdfName.TU) is not null) return false;
        var derived = DeriveTu(annot, pageNumber);
        if (string.IsNullOrEmpty(derived)) return false;
        annot.Put(PdfName.TU, new PdfString(derived));
        return true;
    }

    /// <summary>
    /// Attempt to build the fully-qualified field name by walking the
    /// <c>/Parent</c> chain and joining <c>/T</c> partial names with <c>.</c>.
    /// Returns the page-based fallback when no <c>/T</c> exists on the widget
    /// or any ancestor.
    /// </summary>
    private static string DeriveTu(PdfDictionary widget, int pageNumber)
    {
        var parts = new List<string>();
        var cur = widget;
        // Guard against malformed /Parent cycles.
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (cur is not null && seen.Add(cur))
        {
            var t = cur.GetAsString(PdfName.T);
            var name = t?.ToUnicodeString();
            if (!string.IsNullOrWhiteSpace(name)) parts.Add(name);
            cur = cur.GetAsDictionary(PdfName.Parent);
        }
        if (parts.Count == 0) return $"Form field on page {pageNumber}";
        parts.Reverse();
        return string.Join(".", parts);
    }
}
