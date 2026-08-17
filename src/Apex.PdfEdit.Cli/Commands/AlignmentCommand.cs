using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Layout;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

public static class AlignmentCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to a document.json file");
        var cmd = new Command("alignment", "Classify content-bearing nodes as LEFT / CENTER / RIGHT / JUSTIFIED / UNKNOWN")
        {
            docArg
        };
        cmd.SetHandler(doc => Run(doc.FullName), docArg);
        return cmd;
    }

    public static int Run(string docPath)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            }));
        var log = loggerFactory.CreateLogger("alignment");

        var swTotal = Stopwatch.StartNew();
        log.LogInformation("[alignment] start  doc={Doc}", docPath);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[alignment] loaded document.json  nodes={Nodes}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var classified = new AlignmentDetector().Detect(doc);
        log.LogInformation("[alignment] classified  nodes={N}  ({Ms} ms)", classified.Count, sw.ElapsedMilliseconds);

        var hist = new Dictionary<Alignment, int>();
        foreach (var v in classified.Values)
        {
            hist.TryGetValue(v, out var c);
            hist[v] = c + 1;
        }

        var fileName = Path.GetFileName(docPath);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fileName}:"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  classified nodes: {classified.Count}"));
        foreach (var al in Enum.GetValues<Alignment>())
        {
            var n = hist.GetValueOrDefault(al, 0);
            var pct = classified.Count == 0 ? 0.0 : (n * 100.0 / classified.Count);
            var label = al.ToString().ToUpperInvariant().PadRight(10);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {label} {n,6}  ({pct,5:F1} %)"));
        }

        log.LogInformation("[alignment] complete  totalMs={Ms}", swTotal.ElapsedMilliseconds);
        return 0;
    }
}
