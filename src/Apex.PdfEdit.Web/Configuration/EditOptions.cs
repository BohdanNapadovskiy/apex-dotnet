namespace Apex.PdfEdit.Web.Configuration;

/// <summary>
/// Bindable options for the <c>/edit</c> endpoint. Maps Java's three
/// <c>apex.*</c> Spring properties:
/// <list type="bullet">
///   <item><c>apex.samples.dir</c> → <see cref="SamplesDir"/></item>
///   <item><c>apex.edit.dir</c> → <see cref="EditDir"/></item>
///   <item><c>apex.edit.output-suffix</c> → <see cref="OutputSuffix"/></item>
/// </list>
/// Bound from the <c>Apex:Edit</c> section in <c>appsettings.json</c>.
/// </summary>
public sealed class EditOptions
{
    public const string SectionName = "Apex:Edit";

    /// <summary>Root for resolving relative documentFolder / editsFile / sourceFile paths.</summary>
    public string SamplesDir { get; set; } = string.Empty;

    /// <summary>Root for resolving relative outputFolder paths.</summary>
    public string EditDir { get; set; } = string.Empty;

    /// <summary>Suffix appended to the source PDF's stem to build the output filename.</summary>
    public string OutputSuffix { get; set; } = "_edit.pdf";
}
