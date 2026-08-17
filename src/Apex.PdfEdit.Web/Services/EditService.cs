using System.Diagnostics;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Web.Services;

/// <summary>
/// Runs the same edit pipeline the CLI drives, from raw filesystem paths through to a
/// byte stream of edited PDF. Extracted from the controller so the HTTP layer stays
/// purely about mapping requests/responses.
///
/// Writer routing mirrors <c>edit-ocr</c>: <see cref="ScanDetector"/> decides whether the
/// source is a scanned + OCR-overlaid PDF (<see cref="OcrRevectorizeWriter"/>) or a native
/// tagged PDF (<see cref="SourceBasedWriter"/>).
/// </summary>
public sealed class EditService
{
    private readonly ILogger<EditService> _log;

    public EditService(ILogger<EditService> log)
    {
        _log = log;
    }

    public EditResult Edit(string source, string document, string geometry, string edits, Stream output)
    {
        var swTotal = Stopwatch.StartNew();
        _log.LogInformation("[edit-api] start  in.pdf={Src} doc={Doc} geom={Geom} edits={Edits}",
            Path.GetFileName(source), Path.GetFileName(document),
            Path.GetFileName(geometry), Path.GetFileName(edits));

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(document);
        _log.LogInformation("[edit-api] loaded document.json  nodes={N}  ({Ms} ms)",
            doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var geom = GeometryJsonLoader.Load(geometry);
        _log.LogInformation("[edit-api] loaded geometry.json  pages={P}  ({Ms} ms)",
            geom.PageSearchText?.Count ?? 0, sw.ElapsedMilliseconds);

        sw.Restart();
        var editsJson = EditsJsonLoader.Load(edits);
        _log.LogInformation("[edit-api] loaded edits.json  ops={N}  ({Ms} ms)",
            editsJson.Operations.Count, sw.ElapsedMilliseconds);

        using var resolver = new SourcePdfFontResolver(source);
        _log.LogInformation("[edit-api] resolver opened  sourcePages={P}",
            resolver.SourceDocument.GetNumberOfPages());

        // Scan detection has to happen before the engine so setText's glyph guard uses the
        // widened SkiaSharp-outline check on OCR docs (same reasoning as edit-ocr CLI).
        bool isOcr = ScanDetector.IsScannedOcr(resolver.SourceDocument);
        _log.LogInformation("[edit-api] scan detected: {IsOcr}", isOcr);

        sw.Restart();
        var result = new EditEngine(resolver, isOcr).Apply(doc, geom, editsJson);
        _log.LogInformation("[edit-api] engine applied  ops={Applied}/{Total}  setText={S} addPara={AP} addLI={AL} delete={D} moves={M}  issues={I}  ({Ms} ms)",
            result.AppliedOpIds.Count, editsJson.Operations.Count,
            result.Plan.SetTextOverlays.Count,
            result.Plan.AddParagraphOverlays.Count,
            result.Plan.AddListItemOverlays.Count,
            result.Plan.DeleteOverlays.Count,
            result.Plan.MoveOverlays.Count,
            result.Issues.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        if (isOcr)
        {
            _log.LogInformation("[edit-api] routing through OcrRevectorizeWriter (scan detected)");
            new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, output);
        }
        else
        {
            new SourceBasedWriter(source).Write(result.Plan, output);
        }
        _log.LogInformation("[edit-api] writer done  ({Ms} ms)", sw.ElapsedMilliseconds);

        _log.LogInformation("[edit-api] complete  totalMs={Total}", swTotal.ElapsedMilliseconds);
        return result;
    }
}
