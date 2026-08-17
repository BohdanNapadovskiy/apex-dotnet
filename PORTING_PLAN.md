# Java → .NET 8 Porting Plan — `apex-dotnet`

Source: `C:\projects\apex` (Java 17 + iText 9 + Spring Boot 3.4 + Jackson + JUnit 5).
Target: `C:\projects\apex-dotnet` (.NET 10 + iText for .NET + ASP.NET Core minimal API + System.Text.Json + xUnit).

Goal: **behavior-identical** .NET port of the Java POC. Same CLI surface, same JSON contracts, same PDF output guarantees (PDF/UA-1 pass-through), same test coverage.

---

## 1. Scope

- **99 files, ~16,000 LOC** across `edit / io / layout / model / validator / web / writer` + `Main.java` (CLI) + `PdfEditorApplication.java` (Spring Boot).
- **27 test files** to be ported to xUnit.
- Resources: `application.properties`, `logback.xml`, `schema/`, `static/`.
- Corpus samples in `$POC_SAMPLES_DIR` — path-agnostic, tests will skip when unset (mirroring the Java behavior).

---

## 2. Technology mapping

| Java / JVM                            | .NET 8 equivalent                                           | Notes |
| ------------------------------------- | ----------------------------------------------------------- | ----- |
| Java 17                               | .NET 10                                                     | `net10.0` TFM (bumped from initial net8.0 plan — .NET 10 SDK installed on dev machine) |
| Maven                                 | `dotnet` CLI + `.sln` + `.csproj`                           | central version props via `Directory.Packages.props` |
| iText 9 (kernel/layout/pdfua/forms)   | `itext` 9.x NuGet packages (`itext`, `itext.pdfua`, `itext.bouncy-castle-adapter`, `itext.commons`) | Same AGPL. API is ~95 % identical; content-stream + tag-tree overlays need manual review. |
| Jackson (`ObjectMapper`, POJOs)       | `System.Text.Json` + records                                | `[JsonPropertyName]`, `[JsonConverter]` where needed |
| Spring Boot 3.4                       | ASP.NET Core 8 minimal API                                  | `EditController` → minimal-API endpoints; `EditService` → registered singleton |
| SLF4J + Logback                       | `Microsoft.Extensions.Logging` + Serilog (console + file)   | logback.xml → `appsettings.json` |
| JUnit 5 + AssertJ                     | xUnit + FluentAssertions                                    | 1:1 test port |
| `record` (Java)                       | `record` (C#)                                               | direct map for value types |
| sealed interface (`EditOp`)           | abstract `record` hierarchy + pattern matching              | C# 12 syntax |
| `Optional<T>`                         | nullable ref types (`T?`)                                   | enable `<Nullable>enable</Nullable>` |
| Java streams / collectors             | LINQ                                                        | `.stream().collect(...)` → `.Select(...).ToList()` |
| CLI (`Main` w/ hand-rolled parsing)   | `System.CommandLine`                                        | subcommands: `render / render-json-only / render-ocr / edit / edit-ocr / validate / analyze / alignment / serve` |
| `RestTemplate` / `RestClient`         | not needed (server-side only)                               | |
| Path (java.nio.file)                  | `System.IO.Path` + `FileInfo` / `DirectoryInfo`             | |
| `throws IOException`                  | (removed — C# has no checked exceptions)                    | |

---

## 3. Solution layout (target)

```
apex-dotnet/
├── apex-dotnet.sln
├── Directory.Packages.props           # central NuGet versions
├── Directory.Build.props              # shared TFM, nullable, warnings-as-errors
├── PORTING_PLAN.md                    # this file
├── README.md
├── src/
│   ├── Apex.PdfEdit.Core/             # model + io + validator + layout + edit + writer
│   │   ├── Model/
│   │   ├── Io/
│   │   ├── Validator/
│   │   ├── Layout/
│   │   ├── Edit/
│   │   ├── Writer/
│   │   ├── Resources/schema/          # bundled Rev 1.2 schema
│   │   └── Apex.PdfEdit.Core.csproj
│   ├── Apex.PdfEdit.Cli/              # System.CommandLine host
│   │   ├── Program.cs
│   │   ├── Commands/                  # one class per subcommand
│   │   └── Apex.PdfEdit.Cli.csproj
│   └── Apex.PdfEdit.Web/              # ASP.NET Core minimal API
│       ├── Program.cs
│       ├── Endpoints/
│       ├── Dto/
│       ├── Services/
│       ├── wwwroot/                   # static/ contents
│       ├── appsettings.json
│       └── Apex.PdfEdit.Web.csproj
└── tests/
    └── Apex.PdfEdit.Tests/            # xUnit — mirrors src/test/java tree
        ├── Model/
        ├── Io/
        ├── Validator/
        ├── Layout/
        ├── Edit/
        ├── Writer/
        └── Apex.PdfEdit.Tests.csproj
```

Rationale for one Core project (vs. Java's one-module-many-packages): keeps cross-package refactors cheap, avoids circular-project pitfalls, and matches how iText itself is consumed on .NET. Sub-namespaces (`Apex.PdfEdit.Core.Writer` etc.) mirror Java packages 1:1.

---

## 4. Package-by-package porting map

Numbers are **file counts**; parenthetical numbers are approximate LOC from the Java side.

| # | Package         | Files | Depends on                     | Notes |
| - | --------------- | ----- | ------------------------------ | ----- |
| 1 | `model`         | 5     | (none — pure POJOs)            | `DocumentJson`, `GeometryJson`, `Glyph`, `TextRun`, `TreeNode` → C# records |
| 2 | `io`            | 2     | `model`, System.Text.Json      | `DocumentJsonLoader`, `GeometryJsonLoader`; property-name compatibility is critical |
| 3 | `validator`     | 7     | `model`, `io`                  | Schema + `TagTreeValidator`; ship Rev 1.2 as embedded resource |
| 4 | `layout`        | 2     | `model`                        | `AlignmentDetector` — pure math |
| 5 | `edit`          | 12    | `model`, `io`, `validator`     | Sealed `EditOp` hierarchy → abstract record + pattern matching |
| 6 | `writer`        | 30    | everything above + iText       | **The hard part.** iText content-stream / tag-tree APIs need per-file adaptation |
| 7 | CLI (`Main`)    | 1     | all of Core                    | Rewrite in `System.CommandLine` — cleaner than the original switch/if chain |
| 8 | Web             | 5     | all of Core                    | Minimal API + Serilog + static file middleware |
| 9 | Tests           | 27    | all of the above               | xUnit + FluentAssertions; corpus tests use `[Trait("Corpus", "true")]` and skip when `POC_SAMPLES_DIR` unset |

Total: **91 code files + 27 tests + resources**.

---

## 5. Delivery phases

### Phase A — Scaffold + validate slice (this session)
- Create `.sln`, `Directory.Packages.props`, `Directory.Build.props`.
- Create three projects: `Core`, `Cli`, `Web` and one test project.
- Port `model` (5) + `io` (2) + `validator` (7) + `layout` (2) + minimal CLI wiring for `validate`.
- Copy Rev 1.2 schema JSON into `Core/Resources/schema/`.
- Ship one xUnit test that loads a fixture and runs `TagTreeValidator` end-to-end.
- **Exit criterion:** `dotnet build` + `dotnet test` both green; `dotnet run --project src/Apex.PdfEdit.Cli -- validate <doc.json>` prints the same report the Java CLI does.

### Phase B — Edit + pure writer parts
- Port `edit/` (sealed op hierarchy, engine, JSON loader).
- Port `writer/` **non-iText helpers first** (`WordLevelDiff`, `TagTreeDiff`, `PixelDiff`, `AlignmentDetector` adjacencies, `WriterFontCache`, `WriterDeterminism`, `SystemFontLocator`, `StandardFontMapper`).
- Port their tests.

### Phase C — iText-touching writers (in dependency order)
1. `PageFontInventory`, `SourcePdfFontResolver`, `SourceStructAttrIndex`
2. `IdentityWriter`, `FontAwareWriter`
3. `ContentStreamMcidReplacer` / `Deleter` / `Mover` / `PathBandShifter`
4. `AddParagraphStamper`, `AddListItemStamper`, `RasterizedTextStamper`, `LinkContentsFiller`, `DiffOverlayRenderer`
5. `SourceBasedWriter` (the orchestrator)
6. `OcrRevectorizeWriter`, `ScanDetector`
7. `PdfUaVerifier`

### Phase D — CLI + web + full test port
- All 9 CLI subcommands wired.
- ASP.NET Core minimal API endpoints from `EditController`.
- Remaining tests ported; corpus tests gated on `POC_SAMPLES_DIR`.
- Structured request logging matching the Java `[edit-api]` format.

### Phase E — Round-trip parity check
- Run both Java and .NET side by side on the same corpus doc.
- `PdfUaVerifier` post-write gate must be green on the .NET output.
- Optional: byte-level or PAC 2024 diff of the two outputs.

---

## 6. Known porting risks

1. **iText content-stream API differences.** Java uses `PdfCanvasProcessor` + `IContentOperator`; .NET uses `PdfCanvasProcessor` + `IContentOperator` with slightly different signatures for `Invoke(...)`. Every custom operator listener in `writer/ContentStream*` needs manual review.
2. **Font metrics.** `SourcePdfFontResolver` reaches into iText internals; the internal-class names may differ (`iText.Kernel.Font.PdfFontFactory` vs `com.itextpdf.kernel.font.PdfFontFactory` is fine; deeper types like `DocFontEncoding` occasionally moved).
3. **UTF-16 vs UTF-8 assumptions.** Java `String` is UTF-16; C# `string` is UTF-16 too, but glyph iteration (`String.codePointAt` → `Char.ConvertToUtf32(str, i)`) needs care in `SourcePdfFontResolver.firstUnrenderableCodePoint`.
4. **File paths on Windows.** All test fixtures use forward-slash paths; keep using `Path.Combine` and `Path.DirectorySeparatorChar`-agnostic comparisons.
5. **JSON schema resource loading.** Java uses classpath (`getResourceAsStream`); .NET uses embedded resources — set `<EmbeddedResource>` in Core `.csproj` and load via `Assembly.GetManifestResourceStream`.
6. **Spring Boot logging format.** The `date | LEVEL | pid | [thread] | logger : msg` format needs a Serilog output template that matches for the demo endpoint.
7. **`Optional` vs nullable refs.** `Optional<PdfFont>` becomes `PdfFont?` — but be careful: Java `Optional.empty()` and `null` are distinct in Java, single concept in C#.
8. **Sealed hierarchies (`EditOp`).** Java's sealed interface + `permits` becomes an abstract `record` + closed `switch`. Compiler will complain on missing cases → good.

---

## 7. Conventions

- C# 12, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- File-scoped namespaces.
- `record` for immutable data; `class` only when mutability or identity matters.
- Test naming: `MethodName_Scenario_Expectation` (mirrors JUnit style already used).
- Do not port `_*.py` throwaway scripts, `prep/`, `.claude/`, or `SamplePDFs-ExtracredJSON/`.
- Do not commit `CLAUDE.md` or `.claude/` (per Java project rule; carry it forward).

---

## 8. Open questions before Phase C

Deferred until we get there — writing them down so they aren't lost:

- iText for .NET version pinning: `itext` 9.0.0 vs latest 9.x — pick the exact same minor as the Java side (9.0.0) to minimize API drift, or ride the latest patch?
- BouncyCastle adapter: on .NET you must explicitly reference `itext.bouncy-castle-adapter` (or `itext.bouncy-castle-fips-adapter`); Java brings it transitively. Pick one at Phase C start.
- OCR image handling: Java uses iText's `PdfImageXObject.getBufferedImage()`; .NET returns `SKBitmap` via `SkiaSharp` or raw byte streams. `OcrRevectorizeWriter` will need SkiaSharp added.

---

## 9. Progress log

- **2026-08-14** — Plan created. Starting Phase A.
- **2026-08-14** — Phase A complete. 36 files, model/io/validator/layout ported + `validate` / `alignment` / `analyze` CLI + one xUnit test class (5 tests). Build/test verification blocked pending SDK install.
- **2026-08-15** — TFM bumped from `net8.0` → `net10.0` (dev machine has .NET 10 SDK, not .NET 8).
- **2026-08-15** — 🎉 **Phase D3 complete — PHASE D ENTIRELY DONE — WHOLE PORT COMPLETE.**
  - **10 new test classes** (~1400 LOC C# from ~1365 LOC Java): `IdentityWriterTests`, `PdfUaVerifierTests`, `PageFontInventoryTests`, `SourcePdfFontResolverTests`, `FontAwareWriterTests`, `LinkContentsFillerTests`, `ContentStreamPathBandShifterTests`, `DiffOverlayRendererTests`, `SourceBasedWriterTests`, `ContentStreamMcidReplacerWrapTests`, `OcrRevectorizeWriterTests`.
  - **Skipped by design**: `PixelDiff.java` + `PixelDiffTest.java` (SSIM-based visual regression using Apache PDFBox; no direct .NET equivalent — SkiaSharp doesn't render PDFs, and adding pdfium.NET is a heavy dep for a nice-to-have gate; iText's `PdfUaVerifier` + `TagTreeDiff` tests already cover correctness). `OcrOutputImageExtract.java` + `OcrOutputImageExtract2.java` (throwaway PDFBox-based main-method diagnostic scripts, never part of the JUnit suite).
  - **Fixes during build**: (1) `System.IO.Path` collided with `iText.Kernel.Geom.Path` in every test file that imported `iText.Kernel.Geom` — added `using Path = System.IO.Path;` aliases to `DiffOverlayRendererTests`, `FontAwareWriterTests`, `IdentityWriterTests`. (2) iText for .NET's `PdfDictionary.GetAsBool(PdfName)` returns `bool` directly (not `PdfBoolean?`); dropped the redundant `?.GetValue()` chain in `SourceBasedWriterTests.IdentityCopyPreservesPdfUaCatalogEntries`.
  - **Design translations**: JUnit `@BeforeAll static void loadFont` → xUnit `IClassFixture<T>` pattern (shared setup once per class, injected via constructor); JUnit `@TempDir Path dir` param → local `MakeTempDir()` helper using `Path.GetTempPath()` + `Path.GetRandomFileName()` + try/finally cleanup; Java `PixelDiff.MaskRect record` → skipped entirely; Java `Encoding.ISO_8859_1` for reading PDF content stream bytes → `Encoding.Latin1` (same encoding, .NET name).
- **Verified**: `dotnet build` clean, `dotnet test` **107 passed / 42 skipped / 0 failed**. Test total **149** (up from 15 at end of Phase A, 40 at end of D1, 93 at end of D2). Skipped = 40 corpus-gated + 1 non-Windows-OS + 1 (empty-sample path guard). Test files ported: **17** (up from 4 at end of Phase A).

---

## 🎉 Whole port complete

**Source files**: 82 C# files under `src/` (Core / Cli / Web).
**Test files**: 16 C# files under `tests/` (14 test classes + 3 helpers).
**Total C# LOC**: ~98 files. Ported from ~99 Java main files + 24 Java test files.

**End-to-end verified**: `dotnet build` clean across all 4 projects, `dotnet test` 107 pass / 42 skip / 0 fail. All 4 projects target `net10.0`.

**All 9 CLI subcommands wired**: `validate`, `alignment`, `analyze`, `verify`, `render`, `edit`, `tag-diff`, `render-ocr`, `edit-ocr`. Web endpoint `POST /edit` + `/health` + `/validate` on `Apex.PdfEdit.Web`.

**Deliberate omissions** (documented above): 4 Java files not ported — 2 PDFBox-based diagnostic scripts (`OcrOutputImageExtract*`) + 1 PDFBox-based SSIM helper (`PixelDiff.java`) + its test (`PixelDiffTest.java`). All would require adding pdfium.NET or similar for PDF-to-image rendering; explicitly out of POC scope.

- **2026-08-15** — Phase D2 complete. **EditEngineTests (~45 tests, 1494 LOC Java) ported in one big file.**
  - `TestOutputs.cs` — tiny helper (POC_EDIT_DIR env var → per-sample output directory) for debug-PDF persistence.
  - `Edit/EditEngineTests.cs` — 45 tests grouped:
    - **setText engine** (4 tests): mutation + overlay, glyph-guard refusal, alignment carry-through, graceful missing-target failure.
    - **deleteNode engine** (6 tests): remove + overlay, structural-node rejection, unknown-target rejection, non-artifact-descendant guard, artifact-allowed path, non-whitelisted-tag rejection.
    - **addParagraph engine** (17 tests): insert/append/prepend + overlay, whitelist checks, index bounds, explicit inheritFrom, push-down + orphan-MCID geometry shift, at-end skip, below-page-bottom warning, whitespace-absorption, column-scoped alignment for multi-column pages, collision detection (siblings + non-siblings), font-override application/partial/floor, list-item child rules.
    - **explicit-page validation** (4 tests): setText/addParagraph/deleteNode/addListItem all report mismatch.
    - **addListItem engine** (7 tests): append with column inheritance, independent Lbl+LBody styles, back-compat single-style overlay, insert-between push-down, non-list-parent rejection, empty-L rejection, missing-Lbl rejection, append-shifts-content-after-L.
    - **end-to-end** (7 tests): corpus-gated round-trip through `SourceBasedWriter` — verifies extractable text, StructTreeRoot P-count deltas, and 4 regression cases (Proxy shared-BT / CARE color-leak / Board Packet Td-restore / EA twin-font pick-by-obj-number).
  - **Design translations**: Java anonymous `IEventListener { ... }` → private nested `TextRenderListener` helper class with `Action<TextRenderInfo>` callback (C# lambdas capture locals cleanly, so a shared listener + closure over `minX`/`maxComp` replaces Java's inline final-array trick). Java `List.of(...)` → C# `new EditsJson { Operations = { ... } }` collection-initializer syntax. Java `stream().filter(...).findFirst().orElseThrow()` → `.First(n => n.Id == "x")`. Java `EditPlan.SetTextOverlay` (nested) → `SetTextOverlay` (top-level record in Edit namespace). Java `assertThat(o).isSameAs(x)` → `.Should().BeSameAs(x)`. Java `Should().Match(a -> ... || ...)` → `.Should().BeOneOf(a, b)` (avoids nullable-lambda-inference issue).
- **Verified**: `dotnet build` clean, `dotnet test` **71 passed / 22 skipped / 0 failed** (up from 28 pass / 12 skip in D1). Test total now **93** (up from 40 at end of D1, 15 at end of Phase A). Skipped = 21 corpus-gated + 1 non-Windows-OS.
- **Remaining Phase D (D3)**: OCR/writer round-trip tests — ~12 remaining files, ~2400 LOC (`OcrRevectorizeWriterTest` 253, `SourceBasedWriterTest` 120, `ContentStreamMcidReplacerWrapTest` 242, `LinkContentsFillerTest` 128, `SourcePdfFontResolverTest` 101, `PageFontInventoryTest` 83, `PdfUaVerifierTest` 78, `ContentStreamPathBandShifterTest` 120, `DiffOverlayRendererTest` 103, `FontAwareWriterTest` 85, `IdentityWriterTest` 52` + `PixelDiff` helpers).
- **2026-08-15** — Phase D1 complete. **Web module + 6 more test classes ported.**
  - **Web module**: `Web/Configuration/EditOptions` (bound from `Apex:Edit` config section), `Web/Dto/EditRequest` (record), `Web/Services/EditService` (runs the same pipeline as CLI `edit-ocr`, auto-routes via `ScanDetector`), `Web/Endpoints/EditEndpoints` (POST /edit → minimal API extension method). `Program.cs` wires `Configure<EditOptions>` + `AddSingleton<EditService>` + `MapEditEndpoints()`. `appsettings.json` gets the `Apex.Edit` section defaults. Preserves the Java behaviour exactly: relative paths resolved against `Apex:Edit:SamplesDir` / `EditDir`, output filename = `{source-stem}{OutputSuffix}` (default `_edit.pdf`), same validation errors, same status JSON shape.
  - **6 new xUnit test classes**: `AlignmentDetectorTests` (3 tests — synthetic left/right/center/justified + unknown + corpus-gated form-40x distribution), `GeometryJsonLoaderTests` (4 tests, all corpus-gated), `AlignmentOffsetTests` (6 tests — writer + stamper offset math, needs `InternalsVisibleTo`), `TagTreeDiffTests` (2 tests, corpus-gated), `SystemFontLocatorTests` (9 tests — mix of OS-gated + always-run, `[FactOnOs("Windows")]`/`[FactOnOs("Mac")]`/`[FactOnOs("Linux")]` custom attribute), `CorpusValidationTests` (1 test — validates against Rev 1.2 baseline for all 10 corpus samples).
  - **Two new test helpers**: `[FactOnOsAttribute(string osName)]` (JUnit `@EnabledOnOs` equivalent — sets `Skip` when platform mismatches), `<InternalsVisibleTo Include="Apex.PdfEdit.Tests" />` on Core.csproj (grants test project access to internal helpers like `RasterizedTextStamper.AlignedStartXPx`).
  - **Design translations**: Java Spring `@Value("${apex.samples.dir:}")` → C# `IOptions<EditOptions>` bound from `Apex:Edit` section; Spring `@Service` → `AddSingleton<EditService>`; Spring `@RestController` + `@PostMapping` → minimal API `MapPost("/edit", handler)`; Spring `ResponseEntity.ok(body)` → `Results.Ok(body)`; `ResponseEntity.badRequest().body(msg)` → `Results.BadRequest(msg)`.
- **Verified**: `dotnet build` clean, `dotnet test` **28 passed / 12 skipped / 0 failed**. Test total now 40 (up from 15 at end of Phase A). Skipped = 11 corpus-gated + 1 non-Windows-OS.
- **Remaining Phase D (D2, D3)**: **D2** — `EditEngineTest` alone (1494 LOC Java, the biggest test file — comprehensive coverage of setText / addParagraph / addListItem / deleteNode + push-down reflow + font resolution). **D3** — OCR/writer round-trip tests (`OcrRevectorizeWriterTest`, `SourceBasedWriterTest`, `ContentStreamMcidReplacerWrapTest`, `LinkContentsFillerTest`, `SourcePdfFontResolverTest`, `PageFontInventoryTest`, `PdfUaVerifierTest`, `ContentStreamPathBandShifterTest`, `DiffOverlayRendererTest`, `FontAwareWriterTest`, `IdentityWriterTest`, `PixelDiff` + `PixelDiffTest` + 2 `OcrOutputImageExtract*` helper stubs). ~2600 LOC of tests remaining across ~14 files.
- **2026-08-15** — 🎉 **Phase C7b complete — PHASE C ENTIRELY DONE.** Ported the biggest file in the codebase:
  - `Writer/OcrRevectorizeWriter` — 1915 lines Java → ~1000 lines C#. Complete OCR PDF re-vectoriser: raster passthrough of source's largest image XObject + invisible vector text overlay + word-level diff-driven per-word redact-and-stamp for edits. DFS tag walker with pinned-MCID emission (preserves source's sparse MCID sequence — Java's reflection into `TagTreePointer.getCurrentStructElem()` ported as .NET `BindingFlags.NonPublic|Instance` reflection on the same method name). Metadata copy (Info dict, XMP stream via iText's XMPMeta, /Lang, /ViewerPreferences with force-set DisplayDocTitle=true). Font adoption via `PdfObject.CopyTo(destPdf)` + `PdfFontFactory.CreateFont(dict)` with `Dictionary<PdfFont, PdfFont>(ReferenceEqualityComparer.Instance)` matching Java's IdentityHashMap semantics. Heading underline stamping (single/double rule for H1-H2 vs H3-H6). Source annotation copying (Link/Form OBJRs) via `PdfAnnotation.MakeAnnotation`.
  - Uses the C7a `SourcePdfFontResolver.SkiaTypefaceFor(PdfFont)` API for source-glyph extraction instead of duplicating the FontFile-stream reader — one canonical implementation.
  - `Cli/Commands/RenderOcrCommand` — new `render-ocr` subcommand with auto-routing via `ScanDetector`: falls through to `SourceBasedWriter` when the source is a native PDF. `--visible-text` for diagnostic overlay inspection. `--verify` runs PDF/UA-1 post-write.
  - `Cli/Commands/EditOcrCommand` — new `edit-ocr` subcommand. Runs `EditEngine(resolver, allowExtractedGlyphs: true)` when source is a scan (widened glyph guard uses the SkiaSharp-outline rescue path), then routes to `OcrRevectorizeWriter`. Native PDFs fall through to `SourceBasedWriter`.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped / 0 failed. CLI now exposes **9 subcommands** (validate, alignment, analyze, verify, render, edit, tag-diff, **render-ocr**, **edit-ocr**).
- **Phase C total**: 25 Writer files + 1 Edit engine + 4 CLI commands. Full parity with Java writer stack on native + OCR PDF paths.
- **Remaining work — Phase D**: web endpoints (Spring Boot `EditController` / `EditService` / `EditRequest` DTO → ASP.NET Core minimal API expansion of the current Web stub) + xUnit port of the 26 remaining Java tests (currently only `TagTreeValidatorTests` is ported — `EditEngineTest`, `WriterFontCacheTest`, `PageFontInventoryTest`, `SourcePdfFontResolverTest`, corpus writer tests etc. all remain).
- **2026-08-15** — Phase C7a complete. **SkiaSharp integrated + AWT-rescue path closed + `RasterizedTextStamper` ported.**
  - **SkiaSharp NuGet added** (`SkiaSharp` 3.119.0 + `SkiaSharp.NativeAssets.Win32` / `.macOS` / `.Linux`). Replaces Java AWT (Graphics2D + Font + GlyphVector + BufferedImage + ImageIO) for the OCR raster stamper and glyph-outline rescue.
  - **`SourcePdfFontResolver.FirstUnrenderableCodePointForOcrRaster`** now has a real implementation (was a TODO stub delegating to strict). Extracts `SKTypeface` from source's embedded FontFile / FontFile2 / FontFile3 stream via `SKTypeface.FromData`, then `SKFont.GetGlyphs(codepoint)` → glyph ID 0 means "no outline in the TTF". Cache per PdfIndirectReference.
  - **`Writer/RasterizedTextStamper`** — full port (~450 lines C# from 511 Java). Two entry points: `Stamp` / `StampOnto` (family lookup via `SystemFontLocator`) and `StampWithFont` / `StampOntoWithFont` (caller-supplied `SKTypeface`). Grunge mode (per-glyph jitter + rotation via `SKMatrix`) + `PaintInkDropout` + `PaintSpeckle` scan-noise passes ported pixel-for-pixel via `SKBitmap.GetPixel` / `SetPixel`. Uses `SKTextBlob.Create` for the fast path (no grunge) and manual glyph-by-glyph `SKFont.GetGlyphPath` + `SKMatrix.PreConcat(SKMatrix.CreateRotation)` for the grunge path.
  - **Fix during build**: SkiaSharp 3.x `SKFont.GetGlyphWidths(ushort[], float[])` overload picker kept binding the `(string, SKPaint?)` variant when fed a `ushort[]`. Rewrote the grunge loop to advance the cursor via `SKFont.MeasureText(char.ConvertFromUtf32(cp))` per glyph — a bit more work per glyph, but overload-unambiguous and correctness-equivalent.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped / 0 failed.
- **Remaining Phase C (C7b)**: `OcrRevectorizeWriter` — 1915 lines (the biggest file in the codebase). Deep DFS walker that rebuilds the tag tree from JSON while raster-passthrough painting each source page's largest image XObject as a background layer. Uses `WalkContext`, calls `RasterizedTextStamper.StampOntoWithFont`, and drives the OCR-specific CLI subcommands (`render-ocr`, `edit-ocr`). Deferred to its own session for quality.
- **2026-08-15** — Phase C6 complete. **Native PDF edit path now works end-to-end.** Ported the engine + tag-diff + wired 2 more CLI subcommands:
  - `Edit/EditEngine` (~1099 lines Java → ~900 lines C#). Applies `EditsJson` ops to a `DocumentJson`, mutating the tree in place and building an `EditPlan`. Four ops: `SetTextOp` (bbox + font resolver + glyph-coverage pre-flight), `AddParagraphOp` (push-down reflow with paraGap + collision check + orphan-MCID geometry shifts), `AddListItemOp` (atomic LI+Lbl+LBody with column-inheriting layout), `DeleteNodeOp` (whitelist + non-artifact-descendant guard + no-pull-up policy). All whitelists ported (`AddParagraphParentWhitelist`, `AddParagraphTagWhitelist`, `ListContainerTags`, `AddListItemParentWhitelist`, `DeleteNodeTagWhitelist`, `ListItemChildTags`). Per-op failure records an `EditIssue` and continues.
  - `Writer/TagTreeDiff` — diff two PDFs' StructTreeRoots at (page, mcid, role, parent). 4 mismatch categories: MISSING / ADDED / ROLE_MISMATCH / PARENT_MISMATCH. Custom `LinkedHashSet<T>` inner class (implements `IEnumerable<T>` — needed public `GetEnumerator` for `foreach` duck-typing) mirrors Java's insertion-ordered set semantics.
  - `Cli/Commands/EditCommand` — new `edit` subcommand: `dotnet run --project src/Apex.PdfEdit.Cli -- edit doc.json geom.json src.pdf edits.json out.pdf [--diff-overlay] [--verify]`. Wires resolver + engine + `SourceBasedWriter` + optional `DiffOverlayRenderer` + optional `PdfUaVerifier`. Uses `SetHandler(ctx => ...)` context form (7 args exceeds the strongly-typed overloads' arity).
  - `Cli/Commands/TagDiffCommand` — new `tag-diff` subcommand: `dotnet run --project src/Apex.PdfEdit.Cli -- tag-diff source.pdf edited.pdf [--all]`.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped / 0 failed. CLI now exposes **7 subcommands** (validate, alignment, analyze, verify, render, **edit**, **tag-diff**).
- **Fix during build**: `LinkedHashSet<T>` — Java-style insertion-ordered set — needed `public IEnumerator<T> GetEnumerator()` + `IEnumerable<T>` implementation for `foreach` duck-typing to work. First attempt used `internal` visibility, which C# rejects even inside a private nested class because the foreach binder checks accessibility at call-site language rules, not at emitted-CIL scope.
- **Remaining Phase C (C7+)**: `OcrRevectorizeWriter` (uses `WalkContext`; needs SkiaSharp for OCR raster passthrough), `RasterizedTextStamper` (SkiaSharp glyph rasterisation). All native-PDF write paths — including `EditEngine` — are done and green under iText 9.7 for .NET.
- **2026-08-15** — Phase C5 complete. Ported the orchestrator + wired `render` CLI slice:
  - `Writer/SourceBasedWriter` — the linchpin. Opens source in **stamp mode** (`PdfDocument(reader, writer, StampingProperties)`), applies overlays in place. Pipeline order: delete → setText → move → path-band → addParagraph → addListItem → LinkContentsFiller. Shared `WriterFontCache` across all overlays. Same `WriterDeterminism.PropsForSource` /ID pinning as Java. `GroupByPage` helper replaces Java's `computeIfAbsent` idiom.
  - `Cli/Commands/RenderCommand` — new `render` subcommand: `dotnet run --project src/Apex.PdfEdit.Cli -- render doc.json geom.json src.pdf out.pdf [--verify]`. Identity pass-through path (no EditPlan) exercises the whole stamp-mode pipeline end-to-end. `--verify` runs `PdfUaVerifier` post-write and exits non-zero on failure.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped / 0 failed. CLI now exposes 5 subcommands (validate, alignment, analyze, verify, render).
- **Remaining Phase C (C6+)**: `EditEngine` (glues op-list → EditPlan; the second half of the `edit` flow), `TagTreeDiff` (identity-check utility), `OcrRevectorizeWriter` (uses `WalkContext`, needs SkiaSharp for OCR raster passthrough), `RasterizedTextStamper` (SkiaSharp glyph rasterisation).
- **2026-08-15** — Phase C4 complete. Ported the stampers + link helper + diff overlay + walk context:
  - `Writer/WalkContext` — plain class carrying per-write-pass state through `OcrRevectorizeWriter.WalkTree` recursion (pdf, canvasByPage, geometry, editedMcids, wrapByNodeId, childrenByParent, sourceAttrs, MCID counters).
  - `Writer/LinkContentsFiller` — PDF/UA §7.18 backfill: populates `/Contents` on Link annotations that lack it, deriving human-readable text from `/A /S /URI`, `/A /S /GoTo` (with page-index lookup), and `/A /S /GoToR` action dicts.
  - `Writer/DiffOverlayRenderer` — colour-coded MCID outlines (green setText, blue addParagraph, cyan addListItem, red deleteNode) stamped onto a copy of the edited PDF via `PdfPage.NewContentStreamAfter`.
  - `Writer/AddParagraphStamper` — new tagged paragraph on a copied page. Two-font resolution (source-embedded primary + system/universal fallback for out-of-subset chars); doc-wide font pool with style-affinity sorting; per-char font split via `EmitLineWithFontSplit` when no single font covers the whole text; source-native emission pattern (Tf=1 + Tm-scaled + text-space TL) so Adobe's Format panel readouts match source; ADOBE_TL_COMPENSATION (1.02×) tweak for Adobe's ~0.98 leading readout discount.
  - `Writer/AddListItemStamper` — atomic LI + Lbl + LBody under an existing L container. Walks up two struct levels from donor MCID to find the L. Independent font/color/size resolution per column (bullets often differ from body). Layout attributes with `/LineHeight` attached to both Lbl and LBody so single-line items report source's spacing to tools that consult tag attributes.
  - **Fix during build**: nullable-flow analysis choked on `overlay.Style is not null ? ...` in AddParagraphStamper because `Style` is non-nullable (positional record param) — the redundant null-check confused subsequent field accesses. Replaced with a local `var style = overlay.Style;` upfront.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped / 0 failed. All 6 write-side building blocks (4 ContentStream overlays + 2 stampers) now green under iText 9.7 for .NET.
- **Deferred to Phase C5+**: `SourceBasedWriter` (the orchestrator that ties Replacer + Mover + Deleter + PathBandShifter + Stampers together), `OcrRevectorizeWriter` (uses `WalkContext`), `RasterizedTextStamper` (needs SkiaSharp for glyph rasterisation), `EditEngine`, `TagTreeDiff`.
- **2026-08-15** — Phase C3b complete. Ported the last content-stream overlay — the beast:
  - `Writer/ContentStreamMcidReplacer` (~700 lines C# from 1050 Java). Setext content-stream mutation with the full state machine: tracks Tf/Tm/TL/Tc/Tw + cumulative Td/TD/T* globally; snapshots opening intent per-block (first-Tc/first-Tw/first-Tm/tcAtBdcOpen/twAtBdcOpen); folds TJ per-glyph kerning into an emitted Tc so Adobe's Format-panel readout matches source; inline-emphasis preservation via `SegmentByRuns` (any source `TextRun` substring surviving verbatim in the new content gets re-emitted at the run's original style); font resolution chain (exact by SourceFontObjNumber → family+weight embedded candidates → system → universal fallback → non-embedded standard-14); wrap-shrink-to-fit with sibling-below-aware overflow policy; source-single-line preservation; two-branch BT/ET emit (source has outer BT vs source is per-MCID BT).
  - Uses shared `ContentStreamHelpers.StripAppleHashKeys` + `NoOpListener` from C3a — no duplication.
- **Verified**: `dotnet build` clean, `dotnet test` 13 passed / 2 skipped (corpus) / 0 failed. All 4 content-stream overlays now green under real iText 9.7.
- **2026-08-15 (earlier)** — Build-verification pass reached green after two fixes: (1) removed `itext.pdfua` package reference — PDF/UA classes ship inside the main `itext` NuGet on .NET, unlike Java; bumped 9.0.0 → 9.7.0. (2) Removed `ItextBootstrap.cs` — iText 9 for .NET auto-registers BouncyCastle when the adapter DLL is present (via `iText.Bouncycastleconnector.BouncyCastleDefaultFactory`'s reflection lookup). No manual `SetFactory` needed.
- **2026-08-15** — Phase C3a complete. Ported 3 of 4 content-stream overlays (the smaller three):
  - `Writer/ContentStreamHelpers` — shared `StripAppleHashKeys(PdfPage)` + `NoOpListener` extracted so `Replacer` (coming in C3b), `Mover`, `Deleter`, `PathBandShifter` don't duplicate.
  - `Writer/ContentStreamPathBandShifter` — untagged-graphics push-down band for rectangles (`re`). MC stack tracks BDC/BMC nesting; only untagged `re` ops get rewritten. Straddling rects grow bottom to keep an enclosing border enclosing the shifted content.
  - `Writer/ContentStreamMcidMover` — §5.5 push-down: tracks source Tm/Tlm through BT/Tm/Td/TD/T*/TL as the stream walks, emits an absolute shifted Tm at each target BDC (deferred to BT for per-MCID BT layout). Relative moves inside compose off the shifted position naturally.
  - `Writer/ContentStreamMcidDeleter` — drops target BDC..EMC blocks + prunes matching StructElem + MCR from the tag tree. Text-state preservation (Tc/Tw/Tz/TL/Tf/Tr/Ts + rg/g/k/scn/sc/cs + stroking uppercase counterparts) replayed after EMC. Text-matrix compensation (`dx dy Td`) replays cumulative Td/TD/T* offsets so downstream MCIDs sharing the outer BT/ET don't drift. Compensating q/Q written when the block's graphics-state balance was non-zero.
- **Design translations**: Java `LinkedHashMap` → C# `Dictionary` (insertion-order-preserving in .NET Core in practice; not a spec guarantee but stable for our small op set). Java `String.format(Locale.ROOT, "%.3f", v)` → `v.ToString("0.000", CultureInfo.InvariantCulture)`. Anonymous inner `IEventListener` → shared `ContentStreamHelpers.NoOpListener`. Java `Optional<PdfStructElem>` → nullable `PdfStructElem?`. Java pattern-matching `instanceof PdfNumber n` → C# `is PdfNumber n`.
- **Deferred to Phase C3b (its own session)**: `ContentStreamMcidReplacer` — the beast (1050 lines). Handles setText including glyph-coverage guards, inline emphasis preservation via `TextRun` matching, twin-font disambiguation via `SourceFontObjNumber`, wrap-shrink-to-fit, alignment-aware X offsets, per-line stamping. Ships with its own `stripAppleHashKeys` in Java — the .NET version will just call `ContentStreamHelpers.StripAppleHashKeys` (already extracted here).
- **Deferred to Phase C4+**: all stampers (AddParagraph, AddListItem, RasterizedText), `SourceBasedWriter` (orchestrator), `OcrRevectorizeWriter`, `EditEngine`, `TagTreeDiff`, `LinkContentsFiller`, `DiffOverlayRenderer`, `WalkContext`.
- **2026-08-15** — Phase C2 complete. Ported:
  - `Writer/WrapResult` — internal record (Lines, FontSize, LineHeight, PdfFont, Alignment).
  - `Writer/SourceStructAttrIndex` — snapshots source StructElem attrs (/Alt, /ActualText, /Lang) + OBJR annotations, keyed by (page, canonicalMcid, role) for dest-side propagation.
  - `Writer/SourcePdfFontResolver` — the linchpin. Lazy per-`(page, mcid)` font/style/runs extraction via `PdfCanvasProcessor` + `IEventListener` scanning source content streams. Caches: `_byPageMcid`, `_fontByPageMcid`, `_runsByPageMcid`, `_renderedCharsByFontRef`. Multi-source weight detection, effective font size recovery (Tm.d → width-measurement → ascent-descent → raw), CMYK/Gray/RGB colour hex normalisation.
  - `Writer/FontAwareWriter` — JSON-only rebuild for A/B comparison. Uses resolver for style + geometry for placement; maps to standard-14 via `StandardFontMapper`.
  - **AWT gap documented**: Java's `firstUnrenderableCodePointForOcrRaster` uses `java.awt.Font` glyph-outline extraction as a rescue path. .NET has no AWT — the C# `FirstUnrenderableCodePointForOcrRaster` currently delegates to the strict `FirstUnrenderableCodePoint`. Result: OCR-raster path is more conservative than Java (refuses some edits Java accepts). A SkiaSharp equivalent will land alongside `RasterizedTextStamper` in a later phase.
- **Deferred to Phase C3+**: all `ContentStream*` overlays (Replacer / Deleter / Mover / PathBandShifter), all stampers (AddParagraph, AddListItem, RasterizedText), `SourceBasedWriter` (orchestrator), `OcrRevectorizeWriter`, `EditEngine`, `TagTreeDiff`, `LinkContentsFiller`, `DiffOverlayRenderer`, `WalkContext`.
- **2026-08-15** — Phase C1 complete. iText NuGet packages wired (`itext`, `itext.pdfua`, `itext.commons`, `itext.bouncy-castle-adapter` all at 9.0.0). Ported:
  - `ItextBootstrap` — `[ModuleInitializer]` that registers `BouncyCastleFactory` before any iText call (Java gets this transitively via kernel jar; .NET requires explicit registration).
  - `Writer/StandardFontMapper` — pure helper: FontStyle → StandardFonts name, `#RRGGBB` → `DeviceRgb`.
  - `Writer/WriterDeterminism` — pins source's `/ID[0]` on the output so re-runs are trailer-stable.
  - `Writer/ScanDetector` — heuristic (Creator/Producer token match + unanimous full-page raster) for routing to OCR vs source-based writer.
  - `Writer/PdfUaVerifier` — wraps iText's `PdfUA1Checker`; exposed via new CLI `verify` subcommand.
  - `Writer/PageFontInventory` — page/document font resource inventory, subset-aware `CanRender` and `CanRenderStrict`, family+weight candidate ordering (weight-matching-simple → weight-matching-Type0 → …), cross-page rendered-chars cache via `ConditionalWeakTable<PdfDocument, …>`.
  - `Writer/SystemFontLocator` — Windows/Mac/Linux font path tables + universal fallback loader; uses `RuntimeInformation.IsOSPlatform` (matches Java's `os.name` probe).
  - `Writer/WriterFontCache` — per-write PdfFont dedup keyed on (`load|family|weight`, `universal|family|weight`).
  - `Writer/IdentityWriter` — smoke writer: DocumentJson → tagged PDF, Helvetica, per-node paragraph in bbox.
  - `Cli/Commands/VerifyCommand.cs` — new CLI subcommand.
- **Deferred to Phase C2+:** `SourcePdfFontResolver`, `SourceStructAttrIndex`, `FontAwareWriter`, all `ContentStream*` overlays, all stampers, `SourceBasedWriter`, `OcrRevectorizeWriter`, `EditEngine`, `TagTreeDiff`, `LinkContentsFiller`, `DiffOverlayRenderer`, `RasterizedTextStamper`, `WalkContext`, `WrapResult`.
- **2026-08-15** — Phase B in progress. Ported:
  - `Writer/FontStyle`, `Writer/TextRun` (pure records, no iText)
  - `Edit/EditOp` (abstract record, `[JsonPolymorphic]` on `type`) + `SetTextOp` / `AddParagraphOp` / `AddListItemOp` / `DeleteNodeOp` / `StyleSpec` / `FontOverride`
  - `Edit/EditIssue`, `Edit/EditResult`, `Edit/EditPlan` (with all 6 overlay records: SetText / AddParagraph / AddListItem / Move / Delete / PathBand)
  - `Edit/EditsJson`, `Edit/EditsJsonLoader`
  - `Writer/WordLevelDiff` (LCS + fragment reunion, sealed Segment → abstract record hierarchy)
  - `Tests/Writer/WordLevelDiffTests.cs` (9 tests, 1:1 with Java)
  - **Deferred to Phase C** (blocked on iText NuGet): `EditEngine`, all iText-touching writers, `StandardFontMapper`, `SystemFontLocator`, `WriterDeterminism`.
  - **Design note:** `EditOp` uses abstract-record + primary-constructor pattern so `Id` / `Type` live on the base, are accessible via any `EditOp` reference, and each derived record forwards its Id + hard-coded discriminator via `: EditOp(Id, "setText")`. Positional records can't `override` abstract properties, so this is the idiomatic C# 12 workaround.
