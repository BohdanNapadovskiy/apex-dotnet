using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// <c>render-ocr</c> — scan-aware render. Uses <see cref="OcrRevectorizeWriter"/> when the
/// source looks like a scanned + OCR-overlaid PDF; auto-routes to
/// <see cref="SourceBasedWriter"/> when <see cref="ScanDetector"/> flags it as native.
/// </summary>
public static class RenderOcrCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to document.json");
        var geomArg = new Argument<FileInfo>("geom", "Path to geometry.json");
        var srcArg = new Argument<FileInfo>("source", "Path to the source PDF");
        var outArg = new Argument<FileInfo>("out", "Path to write the output PDF");
        var visibleTextOpt = new Option<bool>("--visible-text",
            "Paint the invisible vector overlay visibly for diagnostic inspection");
        var verifyOpt = new Option<bool>("--verify",
            "Run PDF/UA-1 checker post-write; exit non-zero on failure");

        var cmd = new Command("render-ocr",
            "OCR-aware render: raster passthrough + invisible tag overlay; auto-fallback to source-based for native PDFs")
        {
            docArg, geomArg, srcArg, outArg, visibleTextOpt, verifyOpt
        };
        cmd.SetHandler(ctx =>
        {
            var d = ctx.ParseResult.GetValueForArgument(docArg);
            var g = ctx.ParseResult.GetValueForArgument(geomArg);
            var s = ctx.ParseResult.GetValueForArgument(srcArg);
            var o = ctx.ParseResult.GetValueForArgument(outArg);
            var visible = ctx.ParseResult.GetValueForOption(visibleTextOpt);
            var verify = ctx.ParseResult.GetValueForOption(verifyOpt);
            ctx.ExitCode = Run(d.FullName, g.FullName, s.FullName, o.FullName, visible, verify);
        });
        return cmd;
    }

    public static int Run(string docPath, string geomPath, string sourcePath, string outPath,
        bool visibleText, bool verify)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("render-ocr");

        var swTotal = Stopwatch.StartNew();
        var srcSize = new FileInfo(sourcePath).Length;
        log.LogInformation("[render-ocr] start  in.pdf={Src} ({Bytes}B)  doc={Doc} geom={Geom} out={Out}  visibleText={V}",
            sourcePath, srcSize, Path.GetFileName(docPath), Path.GetFileName(geomPath), outPath, visibleText);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[render-ocr] loaded document.json  nodes={N}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var geom = GeometryJsonLoader.Load(geomPath);
        int pageCount = geom.PageSearchText?.Count ?? 0;
        log.LogInformation("[render-ocr] loaded geometry.json  pages={P}  ({Ms} ms)", pageCount, sw.ElapsedMilliseconds);

        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        using (var resolver = new SourcePdfFontResolver(sourcePath))
        using (var stream = File.OpenWrite(outPath))
        {
            bool isOcr = ScanDetector.IsScannedOcr(resolver.SourceDocument);
            log.LogInformation("[render-ocr] scan detected: {IsOcr}  sourcePages={P}",
                isOcr, resolver.SourceDocument.GetNumberOfPages());
            sw.Restart();
            if (!isOcr)
            {
                log.LogWarning("[render-ocr] auto-routing to source-based writer (native PDF {File})",
                    Path.GetFileName(sourcePath));
                new SourceBasedWriter(sourcePath).Write(stream);
            }
            else
            {
                new OcrRevectorizeWriter(resolver, visibleText).Write(doc, geom, stream);
            }
            log.LogInformation("[render-ocr] writer done  ({Ms} ms)", sw.ElapsedMilliseconds);
        }

        var outBytes = new FileInfo(outPath).Length;
        log.LogInformation("[render-ocr] complete  out={Out} size={Bytes}B  totalMs={Total}",
            outPath, outBytes, swTotal.ElapsedMilliseconds);

        if (verify) return RunPostWriteVerify(log, outPath, "render-ocr");
        return 0;
    }

    private static int RunPostWriteVerify(ILogger log, string outPath, string flow)
    {
        var sw = Stopwatch.StartNew();
        var r = PdfUaVerifier.Verify(outPath);
        if (r.Ok)
        {
            log.LogInformation("[{Flow}] verify pass  ({Ms} ms)", flow, sw.ElapsedMilliseconds);
            return 0;
        }
        log.LogError("[{Flow}] verify FAIL  issues={N}  ({Ms} ms)", flow, r.Issues.Count, sw.ElapsedMilliseconds);
        foreach (var issue in r.Issues)
        {
            log.LogError("[{Flow}]   {Issue}", flow, issue);
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(outPath)}: PDF/UA-1 FAIL"));
        foreach (var issue in r.Issues)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {issue}"));
        }
        return 1;
    }
}
