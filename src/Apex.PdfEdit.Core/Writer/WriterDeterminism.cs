using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Pins the output PDF's document ID array (trailer <c>/ID</c>) to source's
/// initial ID, so identical inputs on repeated invocations produce byte-consistent
/// outputs in the trailer.
///
/// Without this, iText generates a random <c>/ID[1]</c> on every write. That
/// 128-hex-char delta in the trailer also cascades into downstream <c>/Length</c>
/// re-encoding wherever deflate happens to compress the input differently, so a
/// single fresh /ID can produce 15+ bytes of scattered drift across the file.
///
/// Falls back to a plain <see cref="WriterProperties"/> (iText's random-ID default)
/// if source has no /ID or the peek fails — safe pass-through.
///
/// Doesn't freeze <c>/ModDate</c>: iText auto-updates it at
/// <see cref="PdfDocument.Close"/> in stamping mode, and preventing that requires
/// post-close byte editing or reflection — out of POC scope.
/// </summary>
internal static class WriterDeterminism
{
    /// <summary>Peek source's <c>/ID[0]</c> directly from an already-open source document.</summary>
    internal static WriterProperties PropsForSource(PdfDocument? source, ILogger? logger = null)
    {
        if (source is null) return new WriterProperties();
        try
        {
            var id = source.GetTrailer().GetAsArray(PdfName.ID);
            if (id is not null && id.Size() >= 1)
            {
                var init = id.GetAsString(0);
                if (init is not null)
                {
                    return new WriterProperties()
                        .SetInitialDocumentId(init)
                        .SetModifiedDocumentId(init);
                }
            }
        }
        catch (Exception e)
        {
            (logger ?? NullLogger.Instance).LogDebug(
                "Could not read /ID from open source document for determinism: {Msg}", e.Message);
        }
        return new WriterProperties();
    }

    /// <summary>Peek source's <c>/ID[0]</c> via a throwaway readonly open.</summary>
    internal static WriterProperties PropsForSource(string sourcePdf, ILogger? logger = null)
    {
        try
        {
            using var reader = new PdfReader(sourcePdf);
            using var peek = new PdfDocument(reader);
            return PropsForSource(peek, logger);
        }
        catch (Exception e)
        {
            (logger ?? NullLogger.Instance).LogDebug(
                "Could not peek /ID from source {Src} for determinism: {Msg}", sourcePdf, e.Message);
            return new WriterProperties();
        }
    }
}
