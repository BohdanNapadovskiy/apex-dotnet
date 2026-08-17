using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Validator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Apex.PdfEdit.Cli.Commands;

public static class ValidateCommand
{
    public static Command Build()
    {
        var docArg = new Argument<FileInfo>("doc", "Path to a document.json file");
        var cmd = new Command("validate", "Validate a document.json tag tree against the bundled Rev 1.2 schema")
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
        var log = loggerFactory.CreateLogger("validate");

        var swTotal = Stopwatch.StartNew();
        log.LogInformation("[validate] start  doc={Doc}", docPath);

        var sw = Stopwatch.StartNew();
        var doc = DocumentJsonLoader.Load(docPath);
        log.LogInformation("[validate] loaded document.json  nodes={Nodes}  ({Ms} ms)", doc.Tree.Count, sw.ElapsedMilliseconds);

        sw.Restart();
        var schema = SchemaLoader.LoadDefault();
        log.LogInformation("[validate] loaded schema  ({Ms} ms)", sw.ElapsedMilliseconds);

        sw.Restart();
        var report = new TagTreeValidator(schema).Validate(doc);
        log.LogInformation("[validate] validated  errors={E} warnings={W} distinctErrorNodes={D}  ({Ms} ms)",
            report.ErrorCount(), report.WarningCount(), report.DistinctErrorNodes(), sw.ElapsedMilliseconds);

        var fileName = Path.GetFileName(docPath);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fileName}:"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  nodes={doc.Tree.Count}  distinct-error-nodes={report.DistinctErrorNodes()}  errors={report.ErrorCount()}  warnings={report.WarningCount()}"));

        int shown = 0;
        foreach (var issue in report.Issues)
        {
            if (shown++ >= 20)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  ... {report.Issues.Count - 20} more issues"));
                break;
            }
            var sev = issue.Severity.ToString().ToUpperInvariant().PadRight(7);
            var code = issue.Code.PadRight(22);
            var nodeId = (issue.NodeId ?? string.Empty).PadRight(6);
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {sev} {code} node={nodeId} {issue.Message}"));
        }

        log.LogInformation("[validate] complete  totalMs={Ms}", swTotal.ElapsedMilliseconds);
        return 0;
    }
}
