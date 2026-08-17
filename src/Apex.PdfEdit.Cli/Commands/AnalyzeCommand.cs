using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Validator;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// Walks a corpus root looking for <c>*-document.json</c> files and reports per-sample
/// node / error / warning counts under the bundled Rev 1.2 schema.
///
/// The Java <c>analyze --write</c> flag runs a <c>SourceBasedWriter</c> identity smoke test
/// on each sample. That path depends on the writer package, which lands in Phase B/C —
/// <c>--write</c> is accepted but currently a no-op.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Build()
    {
        var rootArg = new Argument<DirectoryInfo?>("corpus-root", () => null,
            "Corpus root directory. Defaults to $POC_SAMPLES_DIR or SamplePDFs-ExtracredJSON/SamplePDFs-ExtracredJSON.");
        var writeOpt = new Option<bool>("--write", "Reserved: enables SourceBasedWriter smoke pass in Phase B+");
        var cmd = new Command("analyze", "Batch-validate every *-document.json under a corpus root")
        {
            rootArg,
            writeOpt
        };
        cmd.SetHandler((root, write) => Run(root?.FullName, write), rootArg, writeOpt);
        return cmd;
    }

    public static int Run(string? corpusRoot, bool write)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("analyze");

        var root = corpusRoot ?? DefaultCorpusRoot();
        if (!Directory.Exists(root))
        {
            log.LogError("Not a directory: {Root}", root);
            return 2;
        }
        if (write)
        {
            log.LogWarning("[analyze] --write is a no-op until Phase B (SourceBasedWriter port)");
        }

        var swTotal = Stopwatch.StartNew();
        log.LogInformation("[analyze] start  root={Root}", root);

        var sw = Stopwatch.StartNew();
        var schema = SchemaLoader.LoadDefault();
        var validator = new TagTreeValidator(schema);
        log.LogInformation("[analyze] loaded schema  ({Ms} ms)", sw.ElapsedMilliseconds);

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Corpus root: {root}"));
        Console.Out.WriteLine();
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"sample",-70} {"nodes",8} {"errors",8} {"warnings",8}"));
        Console.Out.WriteLine(new string('-', 96));

        long grandNodes = 0, grandErrors = 0, grandWarnings = 0;
        int samples = 0, failed = 0;

        foreach (var docPath in Directory.EnumerateFiles(root, "*-document.json", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(docPath) ?? string.Empty);
            var display = name.Length > 68 ? name[..65] + "..." : name;
            try
            {
                var doc = DocumentJsonLoader.Load(docPath);
                var report = validator.Validate(doc);
                grandNodes += doc.Tree.Count;
                grandErrors += report.DistinctErrorNodes();
                grandWarnings += report.WarningCount();
                samples++;
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{display,-70} {doc.Tree.Count,8} {report.DistinctErrorNodes(),8} {report.WarningCount(),8}"));
            }
            catch (Exception e)
            {
                log.LogError("[analyze]   FAIL {Path}: {Msg}", docPath, e.Message);
                failed++;
            }
        }

        Console.Out.WriteLine(new string('-', 96));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{"TOTAL",-70} {grandNodes,8} {grandErrors,8} {grandWarnings,8}"));

        log.LogInformation("[analyze] complete  samples={S} failed={F} nodes={N} errors={E} warnings={W}  totalMs={Ms}",
            samples, failed, grandNodes, grandErrors, grandWarnings, swTotal.ElapsedMilliseconds);
        return 0;
    }

    /// <summary>Resolution order matches Java's TestSamples and Main.defaultCorpusRoot.</summary>
    private static string DefaultCorpusRoot()
    {
        var env = Environment.GetEnvironmentVariable("POC_SAMPLES_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine("SamplePDFs-ExtracredJSON", "SamplePDFs-ExtracredJSON");
    }
}
