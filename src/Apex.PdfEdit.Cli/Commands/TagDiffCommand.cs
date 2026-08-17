using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Apex.PdfEdit.Core.Writer;
using Microsoft.Extensions.Logging;

namespace Apex.PdfEdit.Cli.Commands;

/// <summary>
/// <c>tag-diff</c> subcommand — diff two tagged PDFs' StructTreeRoots at the
/// (page, mcid, role, parent) level. Every mismatch on an identity pass is a bug; on an
/// edited pass the ADDED / MISSING categories may reflect legal add/delete ops and the
/// caller cross-checks against edits.json. Exit 0 when clean, 1 with a mismatch table otherwise.
/// </summary>
public static class TagDiffCommand
{
    public static Command Build()
    {
        var srcArg = new Argument<FileInfo>("source", "Path to the source PDF");
        var editedArg = new Argument<FileInfo>("edited", "Path to the edited PDF");
        var allOpt = new Option<bool>("--all", "Dump every mismatch (default caps at 40 for console readability)");
        var cmd = new Command("tag-diff", "Diff two PDFs' StructTreeRoots at (page, mcid, role, parent)")
        {
            srcArg, editedArg, allOpt
        };
        cmd.SetHandler(
            (src, edited, all) => Run(src.FullName, edited.FullName, all),
            srcArg, editedArg, allOpt);
        return cmd;
    }

    public static int Run(string sourcePath, string editedPath, bool showAll)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        }));
        var log = loggerFactory.CreateLogger("tag-diff");

        var swTotal = Stopwatch.StartNew();
        log.LogInformation("[tag-diff] start  source={Src} edited={Edited}", sourcePath, editedPath);

        var r = TagTreeDiff.Diff(sourcePath, editedPath);
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"source MCIDs={r.SourceMcidCount}  edited MCIDs={r.EditedMcidCount}  mismatches={r.Mismatches.Count}"));
        if (r.Clean)
        {
            Console.Out.WriteLine("CLEAN — tag trees are (page, mcid, role, parent)-identical");
            log.LogInformation("[tag-diff] clean  totalMs={Ms}", swTotal.ElapsedMilliseconds);
            return 0;
        }
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  MISSING={r.MissingCount}  ADDED={r.AddedCount}  ROLE_MISMATCH={r.RoleMismatchCount}  PARENT_MISMATCH={r.ParentMismatchCount}"));

        int limit = showAll ? int.MaxValue : 40;
        int shown = 0;
        foreach (var m in r.Mismatches)
        {
            if (shown++ >= limit)
            {
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  ... {r.Mismatches.Count - limit} more mismatches (use --all to see them)"));
                break;
            }
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {m}"));
        }
        log.LogInformation("[tag-diff] mismatches={N} missing={Miss} added={Add} roleMismatch={RM} parentMismatch={PM}  totalMs={Ms}",
            r.Mismatches.Count, r.MissingCount, r.AddedCount, r.RoleMismatchCount, r.ParentMismatchCount,
            swTotal.ElapsedMilliseconds);
        return 1;
    }
}
