using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Edit;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// <c>edit</c> subcommand — round-trip: load JSON pair + edits.json, apply via
/// <see cref="EditEngine"/>, render via <see cref="SourceBasedWriter"/> onto the source PDF.
/// Optional <c>--diff-overlay</c> stamps colour-coded MCID outlines into a sibling
/// <c>&lt;name&gt;-diff.pdf</c>. Optional <c>--verify</c> runs the PDF/UA-1 checker post-write.
/// </summary>
public static class EditCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to document.json");
        var geomArg = new Argument<FileInfo>("geom", "Path to geometry.json");
        var srcArg = new Argument<FileInfo>("source", "Path to the source PDF");
        var editsArg = new Argument<FileInfo>("edits", "Path to edits.json");
        var outArg = new Argument<FileInfo>("out", "Path to write the edited PDF");
        var diffOverlayOpt = new Option<bool>("--diff-overlay", "Stamp colour-coded MCID outlines into a sibling <name>-diff.pdf");
        var verifyOpt = new Option<bool>("--verify", "Run PDF/UA-1 checker post-write; exit non-zero on failure");

        var cmd = new Command("edit", "Apply edits.json to the source PDF (Scenario 1)")
        {
            docArg, geomArg, srcArg, editsArg, outArg, diffOverlayOpt, verifyOpt
        };
        cmd.SetHandler(
            ctx =>
            {
                var d = ctx.ParseResult.GetValueForArgument(docArg);
                var g = ctx.ParseResult.GetValueForArgument(geomArg);
                var s = ctx.ParseResult.GetValueForArgument(srcArg);
                var e = ctx.ParseResult.GetValueForArgument(editsArg);
                var o = ctx.ParseResult.GetValueForArgument(outArg);
                var diffOverlay = ctx.ParseResult.GetValueForOption(diffOverlayOpt);
                var verify = ctx.ParseResult.GetValueForOption(verifyOpt);
                ctx.ExitCode = Run(d.FullName, g.FullName, s.FullName, e.FullName, o.FullName, diffOverlay, verify);
            });
        return cmd;
    }

    public static int Run(string docPath, string geomPath, string sourcePath, string editsPath, string outPath,
        bool diffOverlay, bool verify)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("edit");

        var swTotal = Stopwatch.StartNew();
        var srcSize = new FileInfo(sourcePath).Length;
        log.LogInformation("[edit] start  in.pdf={Src} ({Bytes}B)  doc={Doc} geom={Geom} edits={Edits} out={Out}  diffOverlay={D} verify={V}",
            sourcePath, srcSize, Path.GetFileName(docPath), Path.GetFileName(geomPath), Path.GetFileName(editsPath),
            outPath, diffOverlay, verify);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[edit] loaded document.json  nodes={N}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var geom = GeometryJsonLoader.Load(geomPath);
        int pageCount = geom.PageSearchText?.Count ?? 0;
        log.LogInformation("[edit] loaded geometry.json  pages={P}  ({Ms} ms)", pageCount, sw.ElapsedMilliseconds);

        sw.Restart();
        var edits = EditsJsonLoader.Load(editsPath);
        log.LogInformation("[edit] loaded edits.json  ops={N}  ({Ms} ms)", edits.Operations.Count, sw.ElapsedMilliseconds);

        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        EditResult result;
        using (var resolver = new SourcePdfFontResolver(sourcePath))
        using (var stream = File.OpenWrite(outPath))
        {
            log.LogInformation("[edit] resolver opened  sourcePages={P}",
                resolver.SourceDocument.GetNumberOfPages());

            sw.Restart();
            result = new EditEngine(resolver).Apply(doc, geom, edits);
            log.LogInformation("[edit] engine applied  ops={Applied}/{Total}  setText={S} addPara={AP} addLI={AL} delete={D} moves={M}  issues={I}  ({Ms} ms)",
                result.AppliedOpIds.Count, edits.Operations.Count,
                result.Plan.SetTextOverlays.Count,
                result.Plan.AddParagraphOverlays.Count,
                result.Plan.AddListItemOverlays.Count,
                result.Plan.DeleteOverlays.Count,
                result.Plan.MoveOverlays.Count,
                result.Issues.Count, sw.ElapsedMilliseconds);

            sw.Restart();
            new SourceBasedWriter(sourcePath).Write(result.Plan, stream);
            log.LogInformation("[edit] writer done  ({Ms} ms)", sw.ElapsedMilliseconds);
        }

        var outBytes = new FileInfo(outPath).Length;
        log.LogInformation("[edit] complete  out={Out} size={Bytes}B  totalMs={Total}",
            outPath, outBytes, swTotal.ElapsedMilliseconds);
        foreach (var i in result.Issues)
        {
            log.LogError("[edit]   FAIL  op={Op}  type={Type}  {Msg}", i.OpId, i.OpType, i.Message);
        }

        if (diffOverlay)
        {
            var diffOut = DiffOverlayRenderer.DiffPathFor(outPath);
            sw.Restart();
            DiffOverlayRenderer.Apply(outPath, diffOut, result.Plan);
            log.LogInformation("[edit] diff overlay written  path={Diff}  ({Ms} ms)", diffOut, sw.ElapsedMilliseconds);
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
            log.LogInformation("[edit] verify pass  ({Ms} ms)", sw.ElapsedMilliseconds);
            return 0;
        }
        log.LogError("[edit] verify FAIL  issues={N}  ({Ms} ms)", r.Issues.Count, sw.ElapsedMilliseconds);
        foreach (var issue in r.Issues)
        {
            log.LogError("[edit]   {Issue}", issue);
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(outPath)}: PDF/UA-1 FAIL"));
        foreach (var issue in r.Issues)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {issue}"));
        }
        return 1;
    }
}
