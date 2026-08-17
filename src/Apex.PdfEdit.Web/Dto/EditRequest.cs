namespace Apex.PdfEdit.Web.Dto;

/// <summary>
/// Folder-based edit request. The client points at:
/// <list type="bullet">
///   <item><c>DocumentFolder</c> — directory containing both <c>*-document.json</c> and
///       <c>*-geometry.json</c>. Server picks up both by glob.</item>
///   <item><c>EditsFile</c> — path to <c>edits.json</c>.</item>
///   <item><c>SourceFile</c> — path to source PDF.</item>
///   <item><c>OutputFolder</c> — directory the edited PDF is written into.</item>
/// </list>
/// Absolute paths used as-is; relative paths resolved against configured roots
/// (see <see cref="Apex.PdfEdit.Web.Configuration.EditOptions"/>).
/// </summary>
public sealed record EditRequest(
    string? DocumentFolder,
    string? EditsFile,
    string? SourceFile,
    string? OutputFolder);
