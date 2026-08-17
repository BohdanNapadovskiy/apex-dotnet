using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Xobject;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Classifies a source PDF as scanned + OCR-overlaid vs native. Used by the CLI's
/// <c>render-ocr</c> / <c>edit-ocr</c> paths to auto-fall-through to
/// <c>SourceBasedWriter</c> when the caller pointed the OCR flow at a native PDF —
/// the OCR writer drops any non-image page content (widgets, vector diagrams, native
/// text runs) that isn't in the tag tree, which is fine for scans but a regression
/// for native PDFs.
///
/// Two independent signals; either one flips the classification:
/// <list type="number">
///   <item><b>Creator / Producer names an OCR-only tool or a scanner</b> —
///       Tesseract / ReadIRIS / CVISION can appear in either field; Xerox WorkCentre,
///       HP ScanJet, Canon CanoScan, Brother MFC, Kyocera, Ricoh, Sharp MX, Epson
///       Scan are checked against Creator only. Deliberately excludes ABBYY /
///       FineReader — those brands are used both as OCR engines AND as
///       PDF/UA-remediation tools that re-tag existing native PDFs, so an ABBYY
///       match alone is ambiguous and needs the scanner Creator to disambiguate.</item>
///   <item><b>Unanimous image dominance</b> — sample the first N pages; <i>every</i>
///       sampled page must have an image XObject whose pixel count exceeds
///       PIXELS_PER_POINT_SQUARED_MIN × page area (roughly a 150+ DPI full-page raster).
///       Real scans have a full-page raster on every page by construction; native PDFs
///       with occasional big cover images fail the unanimous rule because interior
///       pages are blank/vector-only.</item>
/// </list>
///
/// Producer-only tokens were tried in an earlier version and misclassified
/// Workiva-authored, ABBYY-remediated native as a scan. The Creator-vs-Producer split
/// + unanimous image rule is what lets the detector correctly classify all 10
/// corpus samples.
///
/// Cheap by construction — image dominance only reads /XObject /Width + /Height entries,
/// no pixel decode.
/// </summary>
public static class ScanDetector
{
    /// <summary>Substrings that unambiguously flag an OCR-only tool.</summary>
    private static readonly string[] OcrOnlyTokens = { "tesseract", "readiris", "cvision" };

    /// <summary>Substrings that flag the Creator as a physical scanner device.</summary>
    private static readonly string[] ScannerCreatorTokens =
    {
        "workcentre", "scanjet", "canoscan", "brother mfc",
        "kyocera", "ricoh", "sharp mx", "epson scan"
    };

    /// <summary>
    /// Minimum pixel count per pt² of page area for an image XObject to count as
    /// a full-page raster. ~150 DPI: (150/72)² ≈ 4.34; rounded to 5.0.
    /// </summary>
    private const double PixelsPerPointSquaredMin = 5.0;

    /// <summary>Sample size for the image-dominance signal. Unanimous, not majority.</summary>
    private const int PagesToSample = 3;

    public static bool IsScannedOcr(PdfDocument? source)
    {
        if (source is null) return false;
        if (MatchesOcrToolName(source)) return true;
        return AllSampledPagesAreDominantlyImage(source);
    }

    private static bool MatchesOcrToolName(PdfDocument source)
    {
        var info = source.GetTrailer().GetAsDictionary(PdfName.Info);
        if (info is null) return false;
        string? producer = ReadString(info, PdfName.Producer);
        string? creator = ReadString(info, PdfName.Creator);
        if (ContainsAny(producer, OcrOnlyTokens)) return true;
        if (ContainsAny(creator, OcrOnlyTokens)) return true;
        return ContainsAny(creator, ScannerCreatorTokens);
    }

    private static string? ReadString(PdfDictionary info, PdfName key)
    {
        var s = info.GetAsString(key);
        return s?.ToUnicodeString();
    }

    private static bool ContainsAny(string? value, string[] tokens)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var lower = value.ToLowerInvariant();
        foreach (var t in tokens)
        {
            if (lower.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool AllSampledPagesAreDominantlyImage(PdfDocument source)
    {
        int sample = Math.Min(PagesToSample, source.GetNumberOfPages());
        if (sample == 0) return false;
        for (int p = 1; p <= sample; p++)
        {
            if (!IsPageDominantlyImage(source.GetPage(p))) return false;
        }
        return true;
    }

    private static bool IsPageDominantlyImage(PdfPage page)
    {
        var res = page.GetResources();
        if (res is null) return false;
        var names = res.GetResourceNames(PdfName.XObject);
        if (names is null || names.Count == 0) return false;
        var bbox = page.GetPageSize();
        double pageAreaPt = (double)bbox.GetWidth() * (double)bbox.GetHeight();
        if (pageAreaPt <= 0) return false;
        long bestPixels = 0;
        foreach (var name in names)
        {
            var img = res.GetImage(name);
            if (img is null) continue;
            long pixels = (long)img.GetWidth() * (long)img.GetHeight();
            if (pixels > bestPixels) bestPixels = pixels;
        }
        return bestPixels > pageAreaPt * PixelsPerPointSquaredMin;
    }
}
