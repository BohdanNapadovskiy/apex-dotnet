namespace Apex.PdfEdit.Core.Writer;

/// <summary>
/// One contiguous stretch of text within a source MCID that renders with a single
/// <see cref="FontStyle"/>. Multiple runs live inside one BDC when the source PDF
/// switches font / size / colour mid-block — a common pattern for inline emphasis
/// (a bold+coloured phrase inside a plain-body sentence, like 1TCC handbook page 4's
/// "Generally, an <b>illicit discharge</b> is defined as:").
///
/// Captured by SourcePdfFontResolver.ResolveRuns in source order.
/// Consumed by ContentStreamMcidReplacer to preserve inline emphasis on
/// setText: when a run's exact text substring survives verbatim in the new
/// content, the writer re-emits that substring with the run's original style.
///
/// Note: distinct type from <see cref="Apex.PdfEdit.Core.Model.TextRun"/>, which
/// mirrors the customer's <c>*-geometry.json</c> <c>pageSearchText</c> entries.
/// </summary>
public sealed record TextRun(string Text, FontStyle Style);
