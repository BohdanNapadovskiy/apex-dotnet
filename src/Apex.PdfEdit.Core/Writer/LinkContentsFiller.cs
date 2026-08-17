using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Populates the <c>/Contents</c> accessibility text on <c>/Link</c> annotations that
/// lack it. PDF/UA-1 §7.18 requires Link annotations to carry either a <c>/Contents</c>
/// entry or an alternate description via the tag tree — most real-world source PDFs
/// have neither, and stamp-mode identity propagates that gap verbatim.
///
/// Derivation rules:
/// <list type="bullet">
///   <item><c>/A /S /URI</c> — use the <c>/URI</c> string as <c>/Contents</c>.</item>
///   <item><c>/A /S /GoTo</c> with explicit page destination — "Go to page N".</item>
///   <item><c>/A /S /GoTo</c> with named destination — "Go to &lt;name&gt;".</item>
///   <item><c>/A /S /GoToR</c> — "Open &lt;file-path&gt;".</item>
///   <item>Anything else — skipped.</item>
/// </list>
///
/// The pass is idempotent: if <c>/Contents</c> is already set (even to empty), it's left
/// alone.
/// </summary>
public static class LinkContentsFiller
{
    /// <summary>
    /// Populate missing <c>/Contents</c> on every <c>/Link</c> annotation in the document.
    /// Returns the count of annotations patched.
    /// </summary>
    public static int ApplyAll(PdfDocument doc, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var log = logger ?? NullLogger.Instance;
        var pageIndex = BuildPageIndex(doc);
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
                if (PopulateIfMissing(annot, pageIndex)) total++;
            }
        }
        if (total > 0)
        {
            log.LogInformation("[link-contents] patched {N} Link annotation(s) with derived /Contents", total);
        }
        return total;
    }

    /// <summary>
    /// Patch a single annotation dict in place. Returns true when <c>/Contents</c> was
    /// newly set. Safe to call on non-Link dicts.
    /// </summary>
    public static bool PopulateIfMissing(PdfDictionary? annot, IReadOnlyDictionary<PdfIndirectReference, int> pageIndex)
    {
        if (annot is null) return false;
        if (!PdfName.Link.Equals(annot.GetAsName(PdfName.Subtype))) return false;
        // Respect existing /Contents — don't overwrite even if empty.
        if (annot.Get(PdfName.Contents) is not null) return false;
        var derived = DeriveContents(annot, pageIndex);
        if (string.IsNullOrEmpty(derived)) return false;
        annot.Put(PdfName.Contents, new PdfString(derived));
        return true;
    }

    /// <summary>
    /// Attempt to derive a human-readable Contents string from the Link's action dict.
    /// Returns null when no rule matches.
    /// </summary>
    private static string? DeriveContents(PdfDictionary annot, IReadOnlyDictionary<PdfIndirectReference, int> pageIndex)
    {
        var action = annot.GetAsDictionary(PdfName.A);
        if (action is null)
        {
            // Some Link annotations use /Dest directly on the annotation (bypass /A).
            var dest = annot.Get(PdfName.Dest);
            return DescribeDestination(dest, pageIndex);
        }
        var kind = action.GetAsName(PdfName.S);
        if (PdfName.URI.Equals(kind))
        {
            var uri = action.GetAsString(PdfName.URI);
            return uri?.ToUnicodeString();
        }
        if (PdfName.GoTo.Equals(kind))
        {
            return DescribeDestination(action.Get(PdfName.D), pageIndex);
        }
        if (PdfName.GoToR.Equals(kind))
        {
            var fileSpec = action.GetAsDictionary(PdfName.F);
            if (fileSpec is not null)
            {
                var f = fileSpec.GetAsString(PdfName.F);
                if (f is not null) return "Open " + f.ToUnicodeString();
            }
            var direct = action.GetAsString(PdfName.F);
            if (direct is not null) return "Open " + direct.ToUnicodeString();
            return "Open external file";
        }
        return null;
    }

    private static string? DescribeDestination(PdfObject? dest, IReadOnlyDictionary<PdfIndirectReference, int> pageIndex)
    {
        if (dest is null) return null;
        if (dest.IsArray())
        {
            var arr = (PdfArray)dest;
            if (arr.IsEmpty()) return null;
            var target = arr.Get(0);
            if (target?.GetIndirectReference() is { } refr && pageIndex.TryGetValue(refr, out var pageNum))
            {
                return "Go to page " + pageNum;
            }
            return "Internal link";
        }
        if (dest.IsString())
        {
            return "Go to " + ((PdfString)dest).ToUnicodeString();
        }
        if (dest.IsName())
        {
            return "Go to " + ((PdfName)dest).GetValue();
        }
        return "Internal link";
    }

    /// <summary>
    /// Build a page-object → page-number lookup once per doc so we can resolve
    /// <c>/GoTo /D [pageRef ...]</c> destinations to "Go to page N" without scanning
    /// every page dict on every annotation.
    /// </summary>
    private static IReadOnlyDictionary<PdfIndirectReference, int> BuildPageIndex(PdfDocument doc)
    {
        var output = new Dictionary<PdfIndirectReference, int>();
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var refr = doc.GetPage(p).GetPdfObject().GetIndirectReference();
            if (refr is not null) output[refr] = p;
        }
        return output;
    }
}
