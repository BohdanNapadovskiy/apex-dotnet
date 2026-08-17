namespace Apex.PdfEdit.Core.Model;

/// <summary>
/// Mirror of a single node in the customer's <c>document.json</c> flat <c>tree[]</c>.
/// Field names match the JSON (camelCase in the source, mapped via
/// <see cref="System.Text.Json.JsonSerializerOptions.PropertyNamingPolicy"/> = camelCase).
/// </summary>
public sealed class TreeNode
{
    public string? Id { get; set; }
    public string? Parent { get; set; }

    /// <summary>The PDF structure-tree tag role (e.g. "P", "H1", "Sect"). Field is called <c>text</c> in the JSON.</summary>
    public string? Text { get; set; }

    public int Page { get; set; }
    public int Mcid { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public string? Content { get; set; }
    public int Order { get; set; }
    public string? AltText { get; set; }
    public string? ActualText { get; set; }
    public string? Lang { get; set; }
    public bool IsArtifact { get; set; }
    public string? Status { get; set; }

    // Table-related accessibility attributes. Populated by the extractor from the
    // source StructElem's Attributes dict; consumed by OcrRevectorizeWriter's
    // applyStructAttrs so freshly-rebuilt TH/TD/Table StructElems carry the same
    // /Scope, /RowSpan, /ColSpan, /Summary as source. Without these, PAC fails
    // "Table header cell assignments" — every TH cell is treated as having no
    // scope association with its data cells.

    /// <summary><c>"row"</c> / <c>"column"</c> / <c>"both"</c> on TH cells; empty otherwise. Case-insensitive; the writer capitalises to the PDF spec form.</summary>
    public string? Scope { get; set; }

    /// <summary>Space-separated list of associated TH IDs, when the extractor sets it. Rare in the corpus; POC currently doesn't emit /Headers because scope handles the common case.</summary>
    public string? Headers { get; set; }

    /// <summary>Cell span in rows. 0 or 1 means "no span attribute needed".</summary>
    public int RowSpan { get; set; }

    /// <summary>Cell span in columns. 0 or 1 means "no span attribute needed".</summary>
    public int ColSpan { get; set; }

    /// <summary>Human-readable Table summary. Emitted as /Summary text attribute on Table StructElem when non-empty.</summary>
    public string? TableSummary { get; set; }
}
