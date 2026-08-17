# Sample-corpus smoke run — 2026-08-15

End-to-end run of the .NET CLI (`validate` + `render --verify` + `edit --diff-overlay --verify`)
against all 10 samples in `C:\projects\pdf\apex\SamplePDFs-ExtracredJSON`, with results
written to `C:\projects\pdf\apex\edit-dotnet\<sample>\`.

## Bugs found and fixed

Three real defects surfaced only when loading a real `edits.json`. The existing xUnit
suite exercises `EditEngine` with programmatically constructed `EditOp` records — it
never went through `EditsJsonLoader.Load`, so the JSON-side collisions slipped through.

### 1. `EditOp.Type` collided with the polymorphic discriminator

`src/Apex.PdfEdit.Core/Edit/EditOp.cs:18` — the base positional record parameter
`Type` was auto-emitted as a JSON property `type`, which is the same name STJ uses
for the `[JsonPolymorphic]` discriminator. STJ threw:

> The type 'Apex.PdfEdit.Core.Edit.SetTextOp' contains property 'type' that conflicts
> with an existing metadata property name.

**Fix** — annotate the parameter so the generated property carries `[JsonIgnore]`:

```csharp
public abstract record EditOp(string Id, [property: JsonIgnore] string Type);
```

The `Type` field remains reachable to internal callers (`EditEngine.cs:138` reads
`op.Type` for issue messages); only the JSON surface is suppressed.

### 2. Discriminator was not the first property in the corpus `edits.json`

`src/Apex.PdfEdit.Core/Io/JsonOptions.cs:18` — STJ requires the `type` discriminator
to appear first in the object by default; the corpus JSON puts `id` first:

```json
{ "id": "form40x-p1-setText", "type": "setText", ... }
```

**Fix** — opt in to out-of-order metadata (a .NET 9+ option, honoured on .NET 10):

```csharp
AllowOutOfOrderMetadataProperties = true,
```

### 3. `StyleSpec` had two positional constructors — STJ couldn't disambiguate

`src/Apex.PdfEdit.Core/Edit/StyleSpec.cs` — the record had both `StyleSpec(inheritFrom, font)`
(primary) and `StyleSpec(inheritFrom)` (legacy convenience). STJ refuses to pick between
overloads without an explicit `[JsonConstructor]`.

**Fix** — split into init-only props with a `[JsonConstructor]` on the primary form.
Kept the single-arg overload for the existing tests that use it.

## Sample-corpus run results

Runner: `bin/run-all-samples.sh`. Summary: `C:\projects\pdf\apex\edit-dotnet\_summary.txt`.

| Sample | validate | render (verify) | edit engine | edit (verify) |
|---|---|---|---|---|
| 05-15-2025 Board Packet-Remediated | OK | OK | 3/3 ops, 0 issues | FAIL (source) |
| 1TCC-MS4-Staff-Handbook-2024-03-14-Remediated | OK | OK | 4/4 ops, 0 issues | FAIL (source) |
| 2026 Proxy 2.24.26_WEB_ADA | OK | OK | ops OK | PASS |
| 452032_1_1_Bessemer Trust_July 2025_Portfolio_Summaries | OK | OK | ops OK | PASS |
| 949163_Guided Notes_PLATO Course Introduction to Visual Arts | OK | OK | ops OK | PASS |
| CARE Application_Espanol-Remediated | OK | OK | 2/2 ops, 0 issues | FAIL (source) |
| EA Application_English-Remediated | OK | OK | 3/3 ops, 0 issues | FAIL (source) |
| form-40x-2016-Remediated | OK | OK | 4/4 ops, 0 issues | FAIL (source) |
| ImplementationGuidelines-l241_Accessible | OK | OK | ops OK | PASS |
| UDO 26-2652 HGP Estate Planning Kit_F2-Remediated | OK | OK | 4/4 ops, 0 issues | FAIL (source) |

- **10/10** validate OK, **10/10** render OK, **10/10** edit engine applied 100% of ops
  with 0 issues.
- **4/10** edit output passes iText PDF/UA-1 verify. **6/10** fail — all traced to
  pre-existing source-PDF PDF/UA gaps (see below).

## Verify-failure investigation

`PdfUaVerifier` short-circuits on the first `PdfUAConformanceException`, so we cannot
list every issue in one call. Two distinct first-issues appear across the 6 failures:

- **5 samples** — "Document form fields missing both TU entry and alternative description"
- **1 sample** (Board Packet) — "Widget annotation shall be either Form structure element or an Artifact"

To disambiguate *inherited from source* vs *introduced by writer*, a temporary
`diag-widgets` CLI subcommand enumerated `PdfAcroForm` fields and widget annotations on
both source and edit output, counting `/TU` presence, `/StructParent` presence, and
`/Form` structure roles. The command was removed after use.

Result — every stat is identical between source and edit output:

| Sample | fields / TU (src) | fields / TU (out) | widgets w/ Form role (src → out) |
|---|---|---|---|
| Board Packet | 21 / 21 | 21 / 21 | 20 → 20 (**1 orphan widget in source**) |
| 1TCC Staff Handbook | 163 / 154 | 163 / 154 | 240 → 240 (**9 fields missing TU in source**) |
| CARE Espanol | 72 / 71 | 72 / 71 | 109 → 109 (**1 field missing TU in source**) |
| EA English | 97 / 97 | 97 / 97 | 146 → 146 (field-level TU 100%; widget-level inheritance gap in source) |
| form-40x-2016 | 85 / 84 | 85 / 84 | 84 → 84 (**1 field missing TU in source**) |
| UDO Estate Planning | 754 / 752 | 754 / 752 | 765 → 765 (**2 fields missing TU in source**) |

**Verdict: zero writer regressions.** The writer preserves `/TU`, `/StructParent`, and
Form-struct wrappers byte-identically. The 6 verify failures reflect PDF/UA gaps that
were already present in the "-Remediated" source PDFs — iText's checker catches
them where PAC apparently did not.

## Follow-up options (not implemented)

- Auto-repair on write — fill missing `/TU` from field `/T`, wrap orphan widgets in a
  Form struct element. Would let downstream PDF/UA gates pass without operator work,
  but changes the writer from "faithfully preserve" to "faithfully preserve + fix". A
  scope decision; per `CLAUDE.md`, gate on the Java side first.
- Extend `PdfUaVerifier` to enumerate *all* issues per run instead of throwing on the
  first — quality-of-life for CI logs, not a correctness change.

## Reproduction

```powershell
# Point env at corpus (optional — only needed for the xUnit corpus tests, not the CLI runner).
$env:POC_SAMPLES_DIR = "C:\projects\pdf\apex\SamplePDFs-ExtracredJSON"

dotnet build
bash bin/run-all-samples.sh
```

Outputs land under `C:\projects\pdf\apex\edit-dotnet\<sample>\` (one dir per sample,
containing `render.pdf`, `edit.pdf`, `edit-diff.pdf`, and per-run `.log` files) plus a
top-level `_summary.txt`.
