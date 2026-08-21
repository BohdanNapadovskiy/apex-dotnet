# apex-dotnet

.NET 10 port of the Java `apex` PDF-edit POC. See [`PORTING_PLAN.md`](./PORTING_PLAN.md) for the
full phase-by-phase history and design decisions.

## Status

**Port complete.** All phases (A–D) landed: Model / IO / Validator / Layout / Edit / Writer,
9 CLI subcommands, and the ASP.NET Core web API (`/health`, `/validate`, `/edit`).

## Prerequisites

- .NET SDK 10.0 — verify with `dotnet --list-sdks`.

## Build & test

```powershell
dotnet restore
dotnet build
dotnet test
```

### Corpus tests

Sample-dependent tests skip cleanly when the corpus isn't present. To enable them:

```powershell
$env:POC_SAMPLES_DIR = "C:\path\to\SamplePDFs-ExtracredJSON"
dotnet test
```

Optional: `POC_EDIT_DIR` — per-sample debug-output directory for end-to-end tests. Defaults to `bin/`.

## Testing from the CLI

All commands run from the repository root. General shape:

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- <subcommand> [args...]
```

### `validate` — check a document.json against the bundled Rev 1.2 schema

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- validate path\to\document.json
```

### `alignment` — classify content nodes as LEFT / CENTER / RIGHT / JUSTIFIED / UNKNOWN

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- alignment path\to\document.json
```

### `analyze` — batch-validate every `*-document.json` under a corpus root

Corpus root defaults to `$env:POC_SAMPLES_DIR` when omitted.

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- analyze path\to\corpus-root
```

### `render` — identity render (source PDF pass-through via stamp mode)

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- render document.json geometry.json source.pdf out.pdf --verify
```

### `edit` — apply an edits.json to a source PDF

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- edit document.json geometry.json source.pdf edits.json out.pdf --diff-overlay --verify
```

### `render-ocr` / `edit-ocr` — OCR-aware variants

Auto-fallback to the source-based writer for native (non-scanned) PDFs.

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- render-ocr document.json geometry.json source.pdf out.pdf
dotnet run --project src/Apex.PdfEdit.Cli -- edit-ocr   document.json geometry.json source.pdf edits.json out.pdf
```

### `tag-diff` — diff two tagged PDFs at (page, mcid, role, parent)

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- tag-diff source.pdf edited.pdf
```

### `verify` — run the PDF/UA-1 checker on an output PDF

```powershell
dotnet run --project src/Apex.PdfEdit.Cli -- verify out.pdf
```

## Testing the web API (Postman / curl)

Start the server:

```powershell
dotnet run --project src/Apex.PdfEdit.Web
# → http://localhost:8090 (see src/Apex.PdfEdit.Web/Properties/launchSettings.json)
```

Relative paths in request bodies resolve against `Apex:Edit:SamplesDir` / `Apex:Edit:EditDir`
in `src/Apex.PdfEdit.Web/appsettings.json`. Absolute paths are used as-is.

### GET `/health`

- **Postman:** `GET http://localhost:8090/health`

```powershell
curl http://localhost:8090/health
# → {"status":"ok","phase":"D1"}
```

### POST `/validate`

Validates a `document.json` and returns node/error/warning counts.

- **Postman:** `POST http://localhost:8090/validate`, body → raw → JSON:

```json
{
  "documentPath": "C:\\path\\to\\sample-001\\sample-001-document.json"
}
```

```powershell
curl -X POST http://localhost:8090/validate `
  -H "Content-Type: application/json" `
  -d '{"documentPath": "C:\\path\\to\\sample-001\\sample-001-document.json"}'
```

Response:

```json
{ "nodes": 42, "errors": 0, "warnings": 1, "distinctErrorNodes": 0, "clean": false }
```

### POST `/edit`

Runs the full edit pipeline and writes `{source-stem}_edit.pdf` into `outputFolder`.

- **Postman:** `POST http://localhost:8090/edit`, body → raw → JSON:

```json
{
  "documentFolder": "sample-001",
  "editsFile": "sample-001\\edits.json",
  "sourceFile": "sample-001\\source.pdf",
  "outputFolder": "sample-001-out"
}
```

Field notes:

- `documentFolder` — directory containing both `*document.json` and `*geometry.json` (picked up by glob)
- `editsFile` — path to `edits.json`
- `sourceFile` — path to the source PDF
- `outputFolder` — directory the edited PDF is written into (created if missing)

```powershell
curl -X POST http://localhost:8090/edit `
  -H "Content-Type: application/json" `
  -d '{"documentFolder": "sample-001", "editsFile": "sample-001\\edits.json", "sourceFile": "sample-001\\source.pdf", "outputFolder": "sample-001-out"}'
```

Success response (200):

```json
{
  "outputFile": "C:\\...\\sample-001-out\\source_edit.pdf",
  "appliedOps": ["op-1", "op-2"],
  "setTextOverlays": 1,
  "addParagraphOverlays": 0,
  "addListItemOverlays": 0,
  "deleteOverlays": 1,
  "moveOverlays": 0,
  "issues": []
}
```

Failures return 400 with a plain-text reason (missing path, unreadable file, edit failure).

## Layout

```
src/
  Apex.PdfEdit.Core/   # model, io, validator, layout, edit, writer
  Apex.PdfEdit.Cli/    # System.CommandLine host + 9 subcommands
  Apex.PdfEdit.Web/    # ASP.NET Core minimal API — /health, /validate, /edit
tests/
  Apex.PdfEdit.Tests/  # xUnit + FluentAssertions
```
