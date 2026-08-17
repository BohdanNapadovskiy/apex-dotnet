using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// <c>edit-ocr</c> — OCR-aware edit round-trip. Applies edits.json via <see cref="EditEngine"/>
/// (with the OCR-widened glyph guard) then renders via <see cref="OcrRevectorizeWriter"/>.
/// Auto-routes to <see cref="SourceBasedWriter"/> when the source is native.
/// </summary>
public static class EditOcrCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to document.json");
        var geomArg = new Argument<FileInfo>("geom", "Path to geometry.json");
        var srcArg = new Argument<FileInfo>("source", "Path to the source PDF");
        var editsArg = new Argument<FileInfo>("edits", "Path to edits.json");
        var outArg = new Argument<FileInfo>("out", "Path to write the edited PDF");
        var verifyOpt = new Option<bool>("--verify",
            "Run PDF/UA-1 checker post-write; exit non-zero on failure");

        var cmd = new Command("edit-ocr",
            "OCR-aware edit: redact source raster + repaint edited MCIDs; auto-fallback to source-based for native")
        {
            docArg, geomArg, srcArg, editsArg, outArg, verifyOpt
        };
        cmd.SetHandler(ctx =>
        {
            var d = ctx.ParseResult.GetValueForArgument(docArg);
            var g = ctx.ParseResult.GetValueForArgument(geomArg);
            var s = ctx.ParseResult.GetValueForArgument(srcArg);
            var e = ctx.ParseResult.GetValueForArgument(editsArg);
            var o = ctx.ParseResult.GetValueForArgument(outArg);
            var verify = ctx.ParseResult.GetValueForOption(verifyOpt);
            ctx.ExitCode = Run(d.FullName, g.FullName, s.FullName, e.FullName, o.FullName, verify);
        });
        return cmd;
    }

    public static int Run(string docPath, string geomPath, string sourcePath, string editsPath,
        string outPath, bool verify)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("edit-ocr");

        var swTotal = Stopwatch.StartNew();
        var srcSize = new FileInfo(sourcePath).Length;
        log.LogInformation("[edit-ocr] start  in.pdf={Src} ({Bytes}B)  doc={Doc} geom={Geom} edits={E} out={Out}",
            sourcePath, srcSize, Path.GetFileName(docPath), Path.GetFileName(geomPath),
            Path.GetFileName(editsPath), outPath);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[edit-ocr] loaded document.json  nodes={N}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var geom = GeometryJsonLoader.Load(geomPath);
        log.LogInformation("[edit-ocr] loaded geometry.json  pages={P}  ({Ms} ms)",
            geom.PageSearchText?.Count ?? 0, sw.ElapsedMilliseconds);

        sw.Restart();
        var edits = EditsJsonLoader.Load(editsPath);
        log.LogInformation("[edit-ocr] loaded edits.json  ops={N}  ({Ms} ms)",
            edits.Operations.Count, sw.ElapsedMilliseconds);

        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        EditResult result;
        using (var resolver = new SourcePdfFontResolver(sourcePath))
        using (var stream = File.OpenWrite(outPath))
        {
            // Detect scan mode BEFORE the engine runs so setText's glyph guard can pick the
            // widened SkiaSharp-outline rescue on OCR docs.
            bool isOcr = ScanDetector.IsScannedOcr(resolver.SourceDocument);
            log.LogInformation("[edit-ocr] scan detected: {IsOcr}  sourcePages={P}",
                isOcr, resolver.SourceDocument.GetNumberOfPages());

            sw.Restart();
            result = new EditEngine(resolver, isOcr).Apply(doc, geom, edits);
            log.LogInformation("[edit-ocr] engine applied  ops={Applied}/{Total}  setText={S} addPara={AP} addLI={AL} delete={D}  issues={I}  ({Ms} ms)",
                result.AppliedOpIds.Count, edits.Operations.Count,
                result.Plan.SetTextOverlays.Count,
                result.Plan.AddParagraphOverlays.Count,
                result.Plan.AddListItemOverlays.Count,
                result.Plan.DeleteOverlays.Count,
                result.Issues.Count, sw.ElapsedMilliseconds);

            sw.Restart();
            if (!isOcr)
            {
                log.LogWarning("[edit-ocr] auto-routing to source-based writer (native PDF {File})",
                    Path.GetFileName(sourcePath));
                new SourceBasedWriter(sourcePath).Write(result.Plan, stream);
            }
            else
            {
                new OcrRevectorizeWriter(resolver).Write(doc, geom, result.Plan, stream);
            }
            log.LogInformation("[edit-ocr] writer done  ({Ms} ms)", sw.ElapsedMilliseconds);
        }

        var outBytes = new FileInfo(outPath).Length;
        log.LogInformation("[edit-ocr] complete  out={Out} size={Bytes}B  totalMs={Total}",
            outPath, outBytes, swTotal.ElapsedMilliseconds);
        foreach (var i in result.Issues)
        {
            log.LogError("[edit-ocr]   FAIL  op={Op}  type={Type}  {Msg}", i.OpId, i.OpType, i.Message);
        }

        if (verify) return RunPostWriteVerify(log, outPath);
        return 0;
    }

    private static int RunPostWriteVerify(ILogger log, string outPath)
    {
        var sw = Stopwatch.StartNew();
        var r = PdfUaVerifier.Verify(outPath);
        if (r.Ok)
        {
            log.LogInformation("[edit-ocr] verify pass  ({Ms} ms)", sw.ElapsedMilliseconds);
            return 0;
        }
        log.LogError("[edit-ocr] verify FAIL  issues={N}  ({Ms} ms)", r.Issues.Count, sw.ElapsedMilliseconds);
        foreach (var issue in r.Issues)
        {
            log.LogError("[edit-ocr]   {Issue}", issue);
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(outPath)}: PDF/UA-1 FAIL"));
        foreach (var issue in r.Issues)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {issue}"));
        }
        return 1;
    }
}
