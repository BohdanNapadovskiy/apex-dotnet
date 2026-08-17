using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// Wraps <see cref="PdfUaVerifier"/> as a CLI subcommand. Exit code 0 on pass, 1 on fail.
/// Complements PAC 2024 — the iText checker short-circuits on the first exception so
/// returns at most one issue, but covers catalog metadata / structure tree / font
/// embedding at the object level.
/// </summary>
public static class VerifyCommand
{
    public static Command Build()
    {
        var pdfArg = new Argument<FileInfo>("pdf", "Path to the PDF to verify");
        var cmd = new Command("verify", "Run iText's PDF/UA-1 checker against a written PDF")
        {
            pdfArg
        };
        cmd.SetHandler(pdf => Run(pdf.FullName), pdfArg);
        return cmd;
    }

    public static int Run(string pdfPath)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("verify");

        var swTotal = Stopwatch.StartNew();
        log.LogInformation("[verify] start  pdf={Pdf}", pdfPath);

        var r = PdfUaVerifier.Verify(pdfPath);
        var fileName = Path.GetFileName(pdfPath);
        if (r.Ok)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fileName}: PDF/UA-1 OK"));
            log.LogInformation("[verify] pass  totalMs={Ms}", swTotal.ElapsedMilliseconds);
            return 0;
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fileName}: PDF/UA-1 FAIL"));
        foreach (var issue in r.Issues)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {issue}"));
        }
        log.LogWarning("[verify] fail  issues={N}  totalMs={Ms}", r.Issues.Count, swTotal.ElapsedMilliseconds);
        return 1;
    }
}
