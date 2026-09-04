using System.Text;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Shared per-character font-splitting emitter used by every stamper that writes new
/// text into a copied page. Given a preferred (source-embedded) font, an ordered
/// pool of alternate embedded twins, and a guaranteed-embedded fallback, this
/// emitter walks the line, picks the first font that can render each character,
/// batches consecutive same-font characters, and drops <c>/Font size Tf (run)Tj</c>
/// pairs onto the canvas.
///
/// Extracted from <see cref="AddParagraphStamper"/> so <see cref="AddListItemStamper"/>
/// and <see cref="ContentStreamMcidReplacer"/> can share the same behavior — the
/// customer-reported silent-drop bug (2026-09-04) was caused by add-list-item
/// choosing a single source-subset font that lacked glyphs for chars like 'X' /
/// 'Y' / bracket / brace. Routing those chars through the fallback preserves them
/// in the rendered PDF at the cost of a mid-run font swap.
///
/// The caller is responsible for opening the <c>BT ... ET</c> block and positioning
/// via <c>Tm</c> / <c>Td</c>; this helper only issues <c>Tf</c> + <c>Tj</c> pairs.
/// </summary>
internal static class MultiFontLineEmitter
{
    /// <summary>
    /// Split <paramref name="line"/> into runs by which font in the candidate chain
    /// can outline each char, then emit each run as its own <c>/Font size Tf (text)Tj</c>
    /// pair. Runs draw at consecutive positions on the same baseline because they share
    /// the current text matrix.
    ///
    /// When <paramref name="primary"/> is null (no source-embedded font known), the
    /// entire line is emitted with the fallback in a single <c>Tj</c>.
    /// </summary>
    internal static void EmitLine(PdfCanvas canvas, string line,
        PdfFont? primary, IReadOnlyList<PdfFont> pool, PdfFont fallback,
        float fontSize, PageFontInventory inv, PdfPage page)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (primary is null)
        {
            canvas.SetFontAndSize(fallback, fontSize).ShowText(line);
            return;
        }
        var run = new StringBuilder();
        PdfFont? current = null;
        for (int i = 0; i < line.Length;)
        {
            int cp = char.ConvertToUtf32(line, i);
            var ch = char.ConvertFromUtf32(cp);
            var pick = PickFontForChar(ch, primary, pool, fallback, inv, page);
            current ??= pick;
            if (!ReferenceEquals(pick, current))
            {
                canvas.SetFontAndSize(current, fontSize).ShowText(run.ToString());
                run.Clear();
                current = pick;
            }
            run.Append(ch);
            i += char.IsHighSurrogate(line[i]) ? 2 : 1;
        }
        if (run.Length > 0 && current is not null)
        {
            canvas.SetFontAndSize(current, fontSize).ShowText(run.ToString());
        }
    }

    /// <summary>
    /// Try <paramref name="primary"/>, then each pool candidate (registered on the
    /// page's resources on first use), then <paramref name="fallback"/>. Uses the
    /// strict outline check so a cmap-only match on a subset that lacks the actual
    /// glyph outline does NOT win — that leak was the root cause of the customer's
    /// silent-drop report.
    /// </summary>
    internal static PdfFont PickFontForChar(string ch, PdfFont primary,
        IReadOnlyList<PdfFont> pool, PdfFont fallback, PageFontInventory inv, PdfPage page)
    {
        if (inv.CanRenderStrict(primary, ch)) return primary;
        foreach (var candidate in pool)
        {
            if (ReferenceEquals(candidate, primary)) continue;
            if (inv.CanRenderStrict(candidate, ch))
            {
                EnsureFontRegistered(page, candidate);
                return candidate;
            }
        }
        return fallback;
    }

    /// <summary>
    /// Add <paramref name="font"/> to the page's /Resources /Font dict if it isn't
    /// already there so a <c>/Fn size Tf</c> op inside the added block can reference
    /// it. Silent on registration failure — the canvas will still register lazily on
    /// the next SetFontAndSize.
    /// </summary>
    internal static void EnsureFontRegistered(PdfPage page, PdfFont font)
    {
        try
        {
            page.GetResources().AddFont(page.GetDocument(), font);
        }
        catch
        {
            // Fall through — canvas will register on demand.
        }
    }
}
