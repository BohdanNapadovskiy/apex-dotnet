using System.Diagnostics;
using Apex.PdfEdit.Web.Configuration;
using Apex.PdfEdit.Web.Dto;
using Apex.PdfEdit.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Apex.PdfEdit.Web.Endpoints;

/// <summary>
/// POST /edit — accepts a JSON <see cref="EditRequest"/>, runs the edit pipeline, and
/// writes the edited PDF to <c>{outputFolder}/{source-stem}{outputSuffix}</c>. Returns
/// a JSON status with the output path + per-op result summary.
///
/// Relative paths in the request are resolved against configured roots (see
/// <see cref="EditOptions"/>) so the client can stay short when working within the
/// conventional corpus/edit layout.
/// </summary>
public static class EditEndpoints
{
    public static IEndpointRouteBuilder MapEditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/edit", HandleEdit);
        return app;
    }

    private static IResult HandleEdit(
        EditRequest req,
        EditService editService,
        IOptions<EditOptions> optionsAccessor,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("edit-api");
        var options = optionsAccessor.Value;
        var swTotal = Stopwatch.StartNew();

        log.LogInformation("[edit-api] request received  documentFolder={DF} editsFile={EF} sourceFile={SF} outputFolder={OF}",
            req.DocumentFolder, req.EditsFile, req.SourceFile, req.OutputFolder);

        var documentFolder = ResolveDir(req.DocumentFolder, options.SamplesDir);
        var editsFile = ResolveFile(req.EditsFile, options.SamplesDir);
        var sourceFile = ResolveFile(req.SourceFile, options.SamplesDir);
        var outputFolder = ResolveOrCreateDir(req.OutputFolder, options.EditDir, log);
        log.LogInformation("[edit-api] paths resolved  documentFolder={DF} editsFile={EF} sourceFile={SF} outputFolder={OF}",
            documentFolder, editsFile, sourceFile, outputFolder);

        var problem = Validate(documentFolder, editsFile, sourceFile, outputFolder);
        if (problem is not null)
        {
            log.LogWarning("[edit-api] request done  http=400  reason={Reason}  totalMs={Ms}",
                problem, swTotal.ElapsedMilliseconds);
            return Results.BadRequest(problem);
        }

        string documentJson;
        string geometryJson;
        try
        {
            documentJson = FindByGlob(documentFolder!, "*document.json", "document.json");
            geometryJson = FindByGlob(documentFolder!, "*geometry.json", "geometry.json");
        }
        catch (InvalidOperationException e)
        {
            log.LogWarning("[edit-api] request done  http=400  reason={Reason}  totalMs={Ms}",
                e.Message, swTotal.ElapsedMilliseconds);
            return Results.BadRequest(e.Message);
        }

        var outputName = Stem(Path.GetFileName(sourceFile!)) + options.OutputSuffix;
        var outputFile = Path.Combine(outputFolder!, outputName);

        try
        {
            using var output = File.OpenWrite(outputFile);
            var result = editService.Edit(sourceFile!, documentJson, geometryJson, editsFile!, output);
            log.LogInformation("[edit-api] edit OK  applied={A} issues={I} -> {Out}",
                result.AppliedOpIds.Count, result.Issues.Count, outputFile);
            foreach (var i in result.Issues)
            {
                log.LogWarning("[edit-api] edit-issue op={Op} type={Type} msg={Msg}", i.OpId, i.OpType, i.Message);
            }
            log.LogInformation("[edit-api] request done  http=200  out={Out}  totalMs={Ms}",
                outputFile, swTotal.ElapsedMilliseconds);
            return Results.Ok(BuildStatus(outputFile, result));
        }
        catch (Exception e)
        {
            log.LogError(e, "[edit-api] edit failed");
            // Best-effort cleanup: partial file is confusing.
            try { if (File.Exists(outputFile)) File.Delete(outputFile); } catch { /* ignore */ }
            log.LogWarning("[edit-api] request done  http=400  reason=edit failed: {Msg}  totalMs={Ms}",
                e.Message, swTotal.ElapsedMilliseconds);
            return Results.BadRequest("edit failed: " + e.Message);
        }
    }

    /// <summary>
    /// Resolve a client-supplied path against a configured root when the client path is
    /// relative or blank. Blank + no root returns null so the caller can raise a specific
    /// "missing field" error.
    /// </summary>
    private static string? ResolvePath(string? p, string root)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        if (Path.IsPathRooted(p)) return p;
        return string.IsNullOrWhiteSpace(root) ? p : Path.Combine(root, p);
    }

    private static string? ResolveFile(string? p, string root) => ResolvePath(p, root);
    private static string? ResolveDir(string? p, string root) => ResolvePath(p, root);

    private static string? ResolveOrCreateDir(string? p, string root, ILogger log)
    {
        var resolved = ResolvePath(p, root);
        if (resolved is null) return null;
        try
        {
            Directory.CreateDirectory(resolved);
        }
        catch (Exception e)
        {
            log.LogWarning("could not create output folder {Resolved}: {Msg}", resolved, e.Message);
        }
        return resolved;
    }

    private static string? Validate(string? documentFolder, string? editsFile,
        string? sourceFile, string? outputFolder)
    {
        if (documentFolder is null) return "missing path: documentFolder";
        if (editsFile is null) return "missing path: editsFile";
        if (sourceFile is null) return "missing path: sourceFile";
        if (outputFolder is null) return "missing path: outputFolder";
        if (!Directory.Exists(documentFolder))
            return "not a directory: documentFolder = " + documentFolder;
        if (!File.Exists(editsFile))
            return "not a readable file: editsFile = " + editsFile;
        if (!File.Exists(sourceFile))
            return "not a readable file: sourceFile = " + sourceFile;
        if (!Directory.Exists(outputFolder))
            return "not a directory: outputFolder = " + outputFolder;
        return null;
    }

    /// <summary>
    /// Find the first entry in <paramref name="dir"/> whose filename matches
    /// <paramref name="glob"/>. Falls through to a plain filename lookup on miss.
    /// </summary>
    private static string FindByGlob(string dir, string glob, string label)
    {
        try
        {
            foreach (var p in Directory.EnumerateFiles(dir, glob))
            {
                return p;
            }
        }
        catch
        {
            // fall through to fallback
        }
        var fallback = Path.Combine(dir, label);
        if (File.Exists(fallback)) return fallback;
        throw new InvalidOperationException("no file matching " + glob + " in " + dir);
    }

    private static string Stem(string filename)
    {
        int dot = filename.LastIndexOf('.');
        return dot > 0 ? filename[..dot] : filename;
    }

    private static object BuildStatus(string outputFile, Apex.PdfEdit.Core.Edit.EditResult result)
    {
        var issues = result.Issues.Select(i => new
        {
            opId = i.OpId,
            type = i.OpType,
            message = i.Message
        }).ToList();
        return new
        {
            outputFile,
            appliedOps = result.AppliedOpIds,
            setTextOverlays = result.Plan.SetTextOverlays.Count,
            addParagraphOverlays = result.Plan.AddParagraphOverlays.Count,
            addListItemOverlays = result.Plan.AddListItemOverlays.Count,
            deleteOverlays = result.Plan.DeleteOverlays.Count,
            moveOverlays = result.Plan.MoveOverlays.Count,
            issues
        };
    }
}
