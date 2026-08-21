# apex-dotnet — Architecture & Class Reference

Behavior-identical .NET 10 port of the Java `apex` PDF-edit POC. Applies structured edits to
tagged PDFs while preserving the tag tree, ParentTree, fonts, widgets, and PDF/UA-1 compliance.

Stack: **.NET 10** · **iText 9.7 for .NET** · **SkiaSharp 3.119** · **xUnit + FluentAssertions**.

---

## 1. Solution layout

```
apex-dotnet.sln
├── src/
│   ├── Apex.PdfEdit.Core     # Model + Io + Validator + Layout + Edit + Writer
│   ├── Apex.PdfEdit.Cli      # System.CommandLine host, 9 subcommands
│   └── Apex.PdfEdit.Web      # ASP.NET Core minimal API (/edit, /validate, /health)
└── tests/
    └── Apex.PdfEdit.Tests    # xUnit suite mirroring the Java src/test tree
```

### Project dependencies

| Project | References | External packages |
|---|---|---|
| `Apex.PdfEdit.Core` | — | iText 9, SkiaSharp, Microsoft.Extensions.Logging.Abstractions |
| `Apex.PdfEdit.Cli` | Core | System.CommandLine, Microsoft.Extensions.Logging |
| `Apex.PdfEdit.Web` | Core | Serilog.AspNetCore |
| `Apex.PdfEdit.Tests` | Core | xUnit, FluentAssertions |

### Data flow

```
document.json + geometry.json + edits.json + source.pdf
        │
        ▼
  EditEngine.Apply()  ──►  EditPlan (overlay instructions)
        │                        │
        ▼                        ▼
  mutated DocumentJson    SourceBasedWriter (native PDFs, stamp mode)
                          OcrRevectorizeWriter (scanned + OCR PDFs)
                                 │
                                 ▼
                          out.pdf  ──►  PdfUaVerifier / TagTreeDiff / DiffOverlayRenderer
```

---

## 2. Apex.PdfEdit.Core

### 2.1 `Model/` — JSON input models

| Class | Kind | Description |
|---|---|---|
| `DocumentJson` | sealed class | Mirror of the customer's `document.json`: flat `Tree[]` of nodes plus `DocumentProperties` and `Summary`. |
| `GeometryJson` | sealed class | Three parallel views of page glyph data: `PageSearchText` (text runs with per-char bboxes), `PageWordBounds` (flat glyphs), `PageMcidWords` (glyphs keyed by page+MCID). Helper `GlyphsFor()`. |
| `TreeNode` | sealed class | One flat tree node: `Id`, `Parent`, PDF structure role (`Content`), text, page, MCID, bbox (`X/Y/Width/Height`), `Order`, accessibility metadata (`AltText`, `ActualText`, `Lang`, `IsArtifact`) and table attributes (`Scope`, `Headers`, `RowSpan`, `ColSpan`, `TableSummary`). |
| `TextRun` | sealed record | A text run (one per MCID group) with per-character `Glyph` list. |
| `Glyph` | sealed record | Single glyph (`Text`, `X`, `Y`, `Width`, `Height`); custom JSON converter tolerates inconsistent field naming in the corpus (`c` vs `text`). |

### 2.2 `Io/` — loaders

| Class | Kind | Description |
|---|---|---|
| `DocumentJsonLoader` | static class | `Load(path)` → `DocumentJson`. |
| `GeometryJsonLoader` | static class | `Load(path)` → `GeometryJson`. |
| `JsonOptions` | static class | Shared `JsonSerializerOptions` (camelCase, lenient — matches Java Jackson behavior). |

### 2.3 `Validator/` — schema-based tag-tree validation

| Class | Kind | Description |
|---|---|---|
| `Severity` | enum | `Error`, `Warning`. |
| `ValidationIssue` | sealed record | One failure: severity, code, node id, message. Static factories `Error()` / `Warning()`. |
| `ValidationReport` | sealed class | Issue collection with `IsClean()`, `ErrorCount()`, `WarningCount()`, `DistinctErrorNodes()`. |
| `TagRule` | sealed class | Per-tag schema rule: `AllowedParents`, `AllowedChildren`, `RequiredChildren`, `RequiredChecks`. |
| `SchemaRules` | sealed class | Tag rules keyed by tag name (`DefinedTags()`, `IsDefined()`, `Rule()`). |
| `SchemaLoader` | static class | Loads the bundled Rev 1.2 schema resource, or a custom JSON file; handles legacy UTF-16/BOM quirks. |
| `TagTreeValidator` | sealed class | Walks the flat tree; reports `DANGLING_PARENT` (error), `UNKNOWN_TAG` (warning), `PARENT_REJECTS_CHILD` / `CHILD_REJECTS_PARENT` (errors). Not yet implemented: requiredChildren/requiredChecks, MCID uniqueness, ParentTree checks. |

### 2.4 `Layout/` — alignment detection

| Class | Kind | Description |
|---|---|---|
| `Alignment` | enum | `Left`, `Center`, `Right`, `Justified`, `Unknown`. |
| `AlignmentDetector` | sealed class | Classifies content nodes per page using mode-based edge detection with 1-pt buckets (`Detect()`); `ClassifyInColumn()` handles multi-column layouts for style donors. |

### 2.5 `Edit/` — edit operations and engine

| Class | Kind | Description |
|---|---|---|
| `EditOp` | abstract record | Base for polymorphic ops, discriminated by JSON `type` (`[JsonPolymorphic]`/`[JsonDerivedType]`). |
| `SetTextOp` | sealed record | Replace text of an existing node; preserves role, position, font, colour. Optional page cross-check. |
| `AddParagraphOp` | sealed record | Insert a new paragraph under a parent at an index; triggers §5.5 push-down when needed. |
| `AddListItemOp` | sealed record | Insert a new LI under an L container; atomically creates the Lbl+LBody pair with list-aware column geometry. |
| `DeleteNodeOp` | sealed record | Delete a node and its subtree; "no pull-up" mode leaves vacated space empty. |
| `StyleSpec` | sealed record | Style hint for add-ops: `InheritFrom` (sibling donor id) + `Font` overrides. |
| `FontOverride` | sealed record | Per-field font override (`Family`, `Size`, `Weight`, `ColorHex`); null/blank means "use donor value". |
| `EditsJson` / `EditsJsonLoader` | sealed class / static | Top-level `edits.json` model (`SchemaVersion`, `BaseDocument`, `Operations`) and its loader. |
| `EditIssue` | sealed record | Per-op validation failure (`OpId`, `OpType`, `Message`). |
| `EditResult` | sealed class | Outcome of `EditEngine.Apply`: `Plan`, `AppliedOpIds`, `Issues`, `IsOk`. |
| `EditEngine` | sealed class | The brains: validates ops against the tree + geometry, detects collisions, computes push-down shifts, resolves fonts via donor nodes, mutates `DocumentJson`, and emits an `EditPlan`. Internal helpers `PruneShiftChain()`, `EstimateWrappedLineCount()` (exposed to tests via `InternalsVisibleTo`). |
| `EditPlan` | sealed class | Writer instruction set — six overlay collections + fluent builder (`NewBuilder()`, `Empty()`, `IsEmpty`). Nested records: |

`EditPlan` nested overlay records:

| Record | Purpose |
|---|---|
| `SetTextOverlay` | Content-stream text replacement: bbox, new content, style, alignment, glyph baseline Y, next-sibling top Y, source runs (for inline-emphasis preservation). |
| `AddParagraphOverlay` | Draw a brand-new tagged paragraph with fresh MCID + StructElem (donor page/MCID for style). |
| `AddListItemOverlay` | Draw LI+Lbl+LBody under an existing L container; per-part positions and font styling. |
| `MoveOverlay` | Shift an existing MCID block by `(Dx, Dy)` — §5.5 push-down. |
| `DeleteOverlay` | Remove a BDC…EMC block and prune its StructElem. |
| `PathBandOverlay` | Untagged-graphics push-down: translate/grow rectangles below `BandTopY`. |

### 2.6 `Writer/` — PDF rendering and stamping (30 files)

**Top-level writers**

| Class | Kind | Description |
|---|---|---|
| `SourceBasedWriter` | sealed class | Primary path. Opens the source in stamp mode (PdfReader + PdfWriter on one PdfDocument) and applies overlays in place — preserves tag tree, ParentTree, widgets, annotations, embedded fonts. `Write(plan, output)`. |
| `IdentityWriter` | sealed class | Rebuilds a tagged PDF from scratch from document.json + geometry.json (no source PDF; no widgets/images). |
| `FontAwareWriter` | sealed class | `IdentityWriter` variant that embeds fonts resolved from a source PDF. |
| `OcrRevectorizeWriter` | sealed class | For scanned+OCR PDFs: keeps the raster, redacts edited regions, repaints edited MCIDs as vector text from geometry glyphs. Internal `AlignedStartX()`. |

**Content-stream mutators (internal static)**

| Class | Description |
|---|---|
| `ContentStreamMcidReplacer` | Rewrites Tj/TJ operators inside a target BDC…EMC block; preserves inline runs when substrings survive verbatim; wraps text to the bbox (`WrapText()`). |
| `ContentStreamMcidMover` | Shifts a BDC…EMC block via `q 1 0 0 1 dx dy cm … Q`. |
| `ContentStreamMcidDeleter` | Removes a target BDC…EMC block and prunes the StructElem/MCR pair. |
| `ContentStreamPathBandShifter` | Translates/grows untagged rectangles below or straddling the push-down band. |
| `ContentStreamHelpers` | Shared parsing/mutation utilities (incl. `NoOpListener` for `PdfCanvasProcessor`). |
| `WalkContext` | Stateful visitor used while walking content-stream operators. |
| `WrapResult` | Internal record — result of text wrapping. |

**Stampers (internal static)**

| Class | Description |
|---|---|
| `AddParagraphStamper` | Draws a new tagged paragraph: locates the parent StructElem, mints a fresh MCID + StructElem, paints text with the resolved font. |
| `AddListItemStamper` | Draws LI+Lbl+LBody: two-level struct walk-up to the L container, three new StructElems, two MCIDs. |
| `RasterizedTextStamper` | OCR path: rasterizes text via SkiaSharp for re-vectorization (internal `AlignedStartXPx`). |

**Fonts**

| Class | Kind | Description |
|---|---|---|
| `SourcePdfFontResolver` | sealed class, `IDisposable` | Extracts per-(page, mcid) font/style — family, size, weight, colour, leading ratio, glyph outlines — from source content streams. `Resolve()`, `ResolveRuns()`, `FirstUnrenderableCodePoint()`; SkiaSharp raster fallback for incomplete subsets. |
| `PageFontInventory` | sealed class | Enumerates a page's font resources; family-stem matching (strips subset prefix + weight suffix). |
| `WriterFontCache` | sealed class | Caches `(family, weight)` → `PdfFont` so multiple overlays don't re-embed subsets. |
| `SystemFontLocator` | static class | Cross-platform system font file lookup (Windows / macOS CoreText / Linux fontconfig). |
| `StandardFontMapper` | static class | Maps family+weight onto iText's 14 standard fonts. |
| `FontStyle` | sealed record | `Family`, `Size`, `Weight`, `ColorHex`, `SourceFontObjNumber`, `LeadingRatio`. |
| `TextRun` (Writer) | sealed record | Source run + inline `FontStyle` (distinct from `Model.TextRun`). |

**Verification / diff**

| Class | Kind | Description |
|---|---|---|
| `PdfUaVerifier` | static class | Wraps iText's PDF/UA-1 checker; returns `VerifyResult(Ok, Issues)`. |
| `TagTreeDiff` | static class | Diffs two PDFs' StructTreeRoots at `(page, mcid, role, parent)` — ADDED / MISSING / CHANGED. |
| `WordLevelDiff` | static class | Glyph-list vs new-content diff producing Replace/Keep segments for redact-and-stamp. |
| `DiffOverlayRenderer` | static class | Stamps colour-coded MCID outlines into `<name>-diff.pdf` for visual inspection. |

**Compliance auto-fixers & misc**

| Class | Kind | Description |
|---|---|---|
| `FormFieldTuFiller` | internal static | Fills missing `/TU` (tooltip) on form widgets from field `/T`. |
| `LinkContentsFiller` | internal static | Fills missing `/Contents` on Link annotations from the `/URI` action. |
| `OrphanWidgetTagger` | internal static | Wraps untagged form widgets in a `Form` StructElem. |
| `SourceStructAttrIndex` | internal sealed class | Caches source StructElem table attributes (Scope, Headers, RowSpan, ColSpan, Summary) for re-emission on rebuild. |
| `ScanDetector` | static class | Heuristic `IsScannedOcr()` — invisible OCR text overlay + full-page raster. Routes native vs OCR writer. |
| `WriterDeterminism` | static class | Deterministic writer properties for reproducible output (`PropsForSource()`). |

---

## 3. Apex.PdfEdit.Cli

Development harness (`Program.cs` builds the `RootCommand`). Each command is a static class in `Commands/`.

| Command class | Subcommand | Description |
|---|---|---|
| `ValidateCommand` | `validate <doc>` | Validate document.json against the bundled Rev 1.2 schema. Exit 0 clean / 1 errors. |
| `AlignmentCommand` | `alignment <doc>` | Classify content nodes LEFT/CENTER/RIGHT/JUSTIFIED/UNKNOWN. |
| `AnalyzeCommand` | `analyze [root] [--write]` | Batch-validate a corpus (`*-document.json`); per-sample node/error/warning counts. |
| `RenderCommand` | `render <doc> <geom> <src> <out> [--verify]` | Identity pass-through via `SourceBasedWriter`; optional PDF/UA check. |
| `RenderOcrCommand` | `render-ocr … [--visible-text] [--verify]` | Scan-aware render via `OcrRevectorizeWriter`; auto-falls back for native PDFs. |
| `EditCommand` | `edit <doc> <geom> <src> <edits> <out> [--diff-overlay] [--verify]` | Full pipeline: `EditEngine` → `SourceBasedWriter`. |
| `EditOcrCommand` | `edit-ocr … [--verify]` | OCR-aware edit via `OcrRevectorizeWriter`; auto-fallback for native. |
| `TagDiffCommand` | `tag-diff <source> <edited> [--all]` | Struct-tree diff of two tagged PDFs. Exit 0 clean / 1 mismatch. |
| `VerifyCommand` | `verify <pdf>` | Runs `PdfUaVerifier`. Exit 0 pass / 1 fail. |

---

## 4. Apex.PdfEdit.Web

ASP.NET Core minimal API on port 8080.

| File / Class | Description |
|---|---|
| `Program.cs` | DI (singleton `EditService`), Serilog, endpoints: `GET /health`, `POST /validate`, `POST /edit`. |
| `Configuration/EditOptions` | Bound from `Apex:Edit` config section: `SamplesDir`, `EditDir`, `OutputSuffix`. (Java Spring `apex.*` equivalent.) |
| `Dto/EditRequest` | POST body: `DocumentFolder` (globbed for `*-document.json` / `*-geometry.json`), `EditsFile`, `SourceFile`, `OutputFolder`. Relative paths resolve against `EditOptions` roots. |
| `Services/EditService` | Orchestrates the pipeline: load JSONs, open `SourcePdfFontResolver`, `ScanDetector` routing, `EditEngine.Apply`, `OcrRevectorizeWriter` or `SourceBasedWriter`, timing logs. |
| `Endpoints/EditEndpoints` | `MapEditEndpoints()` — resolves paths, validates request, calls `EditService`, returns JSON (outputPath, applied ops, issues, timing). |

---

## 5. Apex.PdfEdit.Tests

149 tests (107 pass, 41 corpus-gated skips, 1 OS-gated skip). Corpus root comes from
`POC_SAMPLES_DIR`; output root from `POC_EDIT_DIR` (default `bin/`).

### Infrastructure

| Class | Description |
|---|---|
| `TestSamples` | Resolves the corpus root; tests skip cleanly when absent. |
| `TestOutputs` | Resolves the per-sample output directory. |
| `FactIfSampleAttribute` | `[FactIfSample("rel/path")]` — skips when the sample file is missing (Java `@EnabledIf`). |
| `FactOnOsAttribute` | `[FactOnOs("Windows")]` — OS-gated tests (Java `@EnabledOnOs`). |

### Test classes

| Class | Covers |
|---|---|
| `Edit/EditEngineTests` | setText / addParagraph / addListItem / deleteNode, push-down reflow, collisions, font resolution, end-to-end writer round-trip. |
| `Io/GeometryJsonLoaderTests` | geometry.json sections and glyph structure. |
| `Layout/AlignmentDetectorTests` | Synthetic alignment classification; multi-column `ClassifyInColumn`. |
| `Validator/TagTreeValidatorTests` | Schema loading + all four issue codes. |
| `Validator/CorpusValidationTests` | .NET validator output vs Python Rev 1.2 baseline on 10 corpus samples. |
| `Writer/SourceBasedWriterTests` | Identity copy preserves pages, tags, text, widgets, ParentTree. |
| `Writer/IdentityWriterTests` | From-scratch tagged rebuild. |
| `Writer/FontAwareWriterTests` | Font embedding via `SourcePdfFontResolver`. |
| `Writer/SourcePdfFontResolverTests` | Per-(page, mcid) style resolution; OCR raster fallback. |
| `Writer/PageFontInventoryTests` | Family-stem stripping; page font enumeration. |
| `Writer/SystemFontLocatorTests` | Per-OS font lookup (`[FactOnOs]`). |
| `Writer/OcrRevectorizeWriterTests` | Raster preservation + extractable vector overlay. |
| `Writer/PdfUaVerifierTests` | PDF/UA-1 pass/fail, Link `/Contents` fix, untagged widget rejection. |
| `Writer/TagTreeDiffTests` | Identity write → clean tag diff. |
| `Writer/WordLevelDiffTests` | Replace/Keep segment computation. |
| `Writer/DiffOverlayRendererTests` | Diff PDF naming + colour-coded outlines. |
| `Writer/FormFieldTuFillerTests` | `/TU` auto-fill, preservation of existing values. |
| `Writer/LinkContentsFillerTests` | `/Contents` auto-fill for URI links; GoTo handling. |
| `Writer/OrphanWidgetTaggerTests` | Untagged widget → `Form` StructElem. |
| `Writer/ContentStreamMcidReplacerWrapTests` | Text wrapping vs bbox width/font. |
| `Writer/ContentStreamPathBandShifterTests` | Band shift/grow rules; tagged content untouched. |
| `Writer/AlignmentOffsetTests` | Left/Right/Center anchor math. |

---

## 6. Related documents

- `PORTING_PLAN.md` — full phase-by-phase port history and design decisions (incl. deliberate
  omissions: `PixelDiff`, OCR image-extract diagnostics).
- `README.md` — quick start.
- Java reference repo at `C:\projects\apex` — authoritative for behavior.
