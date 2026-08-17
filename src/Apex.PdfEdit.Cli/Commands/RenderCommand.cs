using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// <c>render</c> subcommand — source-PDF-as-base identity pass-through via
/// <see cref="SourceBasedWriter"/>. Copies the source PDF verbatim (stamp mode preserves
/// widgets, annotations, embedded fonts, images, form fields, and the tag tree with
/// ParentTree back-references intact).
///
/// Loads <c>document.json</c> and <c>geometry.json</c> so the loader-timing info shows
/// up in logs and so we exercise the same "load pair up front" path the future <c>edit</c>
/// subcommand will use — but the identity flow doesn't consume them yet.
///
/// <c>--verify</c> runs the PDF/UA-1 checker post-write and exits non-zero on any issue.
/// </summary>
public static class RenderCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to document.json");
        var geomArg = new Argument<FileInfo>("geom", "Path to geometry.json");
        var srcArg = new Argument<FileInfo>("source", "Path to the source PDF");
        var outArg = new Argument<FileInfo>("out", "Path to write the output PDF");
        var verifyOpt = new Option<bool>("--verify", "Run PDF/UA-1 checker post-write; exit non-zero on failure");

        var cmd = new Command("render", "Source-PDF-as-base identity pass-through (stamp mode)")
        {
            docArg, geomArg, srcArg, outArg, verifyOpt
        };
        cmd.SetHandler(
            (doc, geom, src, output, verify) => Run(doc.FullName, geom.FullName, src.FullName, output.FullName, verify),
            docArg, geomArg, srcArg, outArg, verifyOpt);
        return cmd;
    }

    public static int Run(string docPath, string geomPath, string sourcePath, string outPath, bool verify)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("render");

        var swTotal = Stopwatch.StartNew();
        var srcSize = new FileInfo(sourcePath).Length;
        log.LogInformation("[render] start  in.pdf={Src} ({Bytes}B)  doc={Doc} geom={Geom} out={Out}",
            sourcePath, srcSize, Path.GetFileName(docPath), Path.GetFileName(geomPath), outPath);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[render] loaded document.json  nodes={N}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var geom = GeometryJsonLoader.Load(geomPath);
        int pageCount = geom.PageSearchText?.Count ?? 0;
        log.LogInformation("[render] loaded geometry.json  pages={P}  ({Ms} ms)", pageCount, sw.ElapsedMilliseconds);

        var parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        sw.Restart();
        using (var stream = File.OpenWrite(outPath))
        {
            new SourceBasedWriter(sourcePath).Write(stream);
        }
        log.LogInformation("[render] writer done  ({Ms} ms)", sw.ElapsedMilliseconds);

        var outBytes = new FileInfo(outPath).Length;
        log.LogInformation("[render] complete  out={Out} size={Bytes}B  totalMs={Total}",
            outPath, outBytes, swTotal.ElapsedMilliseconds);

        if (verify) return RunPostWriteVerify(log, outPath);
        return 0;
    }

    private static int RunPostWriteVerify(ILogger log, string outPath)
    {
        var sw = Stopwatch.StartNew();
        var r = PdfUaVerifier.Verify(outPath);
        if (r.Ok)
        {
            log.LogInformation("[render] verify pass  ({Ms} ms)", sw.ElapsedMilliseconds);
            return 0;
        }
        log.LogError("[render] verify FAIL  issues={N}  ({Ms} ms)", r.Issues.Count, sw.ElapsedMilliseconds);
        foreach (var issue in r.Issues)
        {
            log.LogError("[render]   {Issue}", issue);
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(outPath)}: PDF/UA-1 FAIL"));
        foreach (var issue in r.Issues)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {issue}"));
        }
        return 1;
    }
}
