using System.CommandLine;
using Apex.PdfEdit.Cli.Commands;

namespace Apex.PdfEdit.Cli;

/// <summary>
/// CLI harness for running the pipeline against real samples during POC development.
/// Not a shipped end-user tool.
///
/// Phase A subcommands: <c>validate</c>, <c>alignment</c>, <c>analyze</c>.
/// Phase B–D add: <c>render</c>, <c>render-json-only</c>, <c>render-ocr</c>,
/// <c>edit</c>, <c>edit-ocr</c>, <c>verify</c>, <c>tag-diff</c>, <c>serve</c>.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var root = new RootCommand("apex-pdf-edit — POC harness (.NET)");

        root.AddCommand(ValidateCommand.Build());
        root.AddCommand(AlignmentCommand.Build());
        root.AddCommand(AnalyzeCommand.Build());
        root.AddCommand(VerifyCommand.Build());
        root.AddCommand(RenderCommand.Build());
        root.AddCommand(EditCommand.Build());
        root.AddCommand(TagDiffCommand.Build());
        root.AddCommand(RenderOcrCommand.Build());
        root.AddCommand(EditOcrCommand.Build());

        return root.Invoke(args);
    }
}
