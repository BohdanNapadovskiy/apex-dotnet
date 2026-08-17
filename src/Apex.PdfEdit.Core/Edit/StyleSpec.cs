using System.Text.Json.Serialization;

namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// Style hint attached to an add-op. Two independent knobs:
/// <list type="bullet">
///   <item><see cref="InheritFrom"/> — id of a sibling node to copy resolved (page, mcid) style from.</item>
///   <item><see cref="Font"/> — per-field overrides on top of the donor-resolved style. Any missing
///       subfield falls through to the donor's value, so callers can override just family
///       or just size without restating the whole style block. When <see cref="Font"/> itself is
///       null, the donor-resolved style is used verbatim (current behaviour).</item>
/// </list>
/// </summary>
public sealed record StyleSpec
{
    public string? InheritFrom { get; init; }
    public FontOverride? Font { get; init; }

    [JsonConstructor]
    public StyleSpec(string? inheritFrom, FontOverride? font)
    {
        InheritFrom = inheritFrom;
        Font = font;
    }

    /// <summary>Legacy single-arg convenience — used by tests written before the font block existed.</summary>
    public StyleSpec(string? inheritFrom) : this(inheritFrom, null) { }
}

/// <summary>
/// Per-field font override. All fields optional — null or blank means
/// "use the donor-resolved value". <see cref="Size"/> is nullable so callers can omit it
/// without picking a magic sentinel; values below 4pt are treated as unset to
/// match the size floor <c>AddParagraphStamper</c> already applies.
/// </summary>
public sealed record FontOverride(string? Family, float? Size, string? Weight, string? ColorHex);
