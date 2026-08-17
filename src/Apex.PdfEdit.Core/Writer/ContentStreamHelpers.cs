using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Shared helpers used by every content-stream rewriter (<c>ContentStream*</c> family).
/// Extracted so the small utilities don't drift across classes that reference them.
/// </summary>
internal static class ContentStreamHelpers
{
    /// <summary>
    /// Preview / Adobe Reader on some macOS builds inject Apple-proprietary content-hash
    /// entries (<c>AAPL:PPK</c>, <c>AAPL:PPKHash</c>) into the page dictionary. iText's
    /// stamping writer preserves them by default; when we rewrite the content stream those
    /// hashes are no longer accurate and Preview logs a "invalid content hash" warning on
    /// open. Strip them defensively — they're advisory metadata, not spec-required.
    /// </summary>
    internal static void StripAppleHashKeys(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var pageDict = page.GetPdfObject();
        bool modified = false;
        var ppk = new PdfName("AAPL:PPK");
        if (pageDict.ContainsKey(ppk))
        {
            pageDict.Remove(ppk);
            modified = true;
        }
        var ppkHash = new PdfName("AAPL:PPKHash");
        if (pageDict.ContainsKey(ppkHash))
        {
            pageDict.Remove(ppkHash);
            modified = true;
        }
        if (modified) pageDict.SetModified();
    }

    /// <summary>
    /// <see cref="IEventListener"/> implementation that ignores every event. Every
    /// content-stream rewriter uses <see cref="PdfCanvasProcessor"/> purely for its
    /// operator-parsing loop, not for high-level event synthesis, so the listener
    /// hook is a no-op.
    /// </summary>
    internal sealed class NoOpListener : IEventListener
    {
        private static readonly ICollection<EventType> None = Array.Empty<EventType>();
        public void EventOccurred(IEventData data, EventType type) { }
        public ICollection<EventType> GetSupportedEvents() => None;
    }
}
