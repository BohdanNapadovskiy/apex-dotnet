using Apex.PdfEdit.Core.Layout;
using iText.Kernel.Font;

namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// Word-wrapped layout for an edited node's new content. Both the redaction pass
/// and the drawing pass consult the same layout so the box grows in step with
/// the number of lines. Carries the <see cref="PdfFont"/> used for wrap-time width
/// measurement so the draw pass uses the same font — otherwise the wrap widths
/// and the draw widths would diverge (line 1 fits at wrap-time, overflows at
/// draw-time).
///
/// <see cref="Alignment"/> is the source's inferred alignment (from the overlay's
/// Alignment field); both the draw pass and RasterizedTextStamper apply it as a
/// per-line X offset. Defaults to <see cref="Alignment.Left"/> for null / unknown
/// so nothing regresses to a random position.
/// </summary>
internal sealed record WrapResult(
    IReadOnlyList<string> Lines,
    float FontSize,
    float LineHeight,
    PdfFont Font,
    Alignment Alignment);
