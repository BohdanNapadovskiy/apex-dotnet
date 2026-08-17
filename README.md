# apex-dotnet

.NET 8 port of the Java `apex` PDF-edit POC. See [`PORTING_PLAN.md`](./PORTING_PLAN.md) for the full plan.

## Status

**Phase A — scaffold + validate slice.** Model / IO / Validator / Layout ported. `validate` CLI subcommand + one xUnit test verify the end-to-end path works. Writer / edit / OCR / web endpoints follow in Phases B–D.

## Prerequisites

- .NET SDK 10.0 — verify with `dotnet --list-sdks`.

## Build & test

```powershell
dotnet restore
dotnet build
dotnet test
```

## Run the CLI

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- validate path\to\document.json
```

Available subcommands in Phase A: `validate`, `alignment`, `analyze`. The rest (`render`, `edit`, `render-ocr`, `edit-ocr`, `verify`, `tag-diff`, `serve`) land in later phases.

## Corpus tests

Sample-dependent tests skip cleanly when the corpus isn't present. To enable them:

```powershell
$env:POC_SAMPLES_DIR = "C:\path\to\SamplePDFs-ExtracredJSON"
dotnet test
```

## Layout

```
src/
  Apex.PdfEdit.Core/   # model, io, validator, layout (+ edit, writer in later phases)
  Apex.PdfEdit.Cli/    # System.CommandLine host
  Apex.PdfEdit.Web/    # ASP.NET Core minimal API (stub in Phase A)
tests/
  Apex.PdfEdit.Tests/  # xUnit + FluentAssertions
```
