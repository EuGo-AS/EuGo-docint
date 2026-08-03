# make-golden

Offline generator for the committed golden fixtures in `tests/DocInt.Tests/golden`.
No Azure, no network, no checked-in source blobs — every fixture is synthesized here:
`MiniPdf.cs` hand-writes PDF bytes (objects, xref table, trailer — no PDF library),
`ImageFixtures.cs` draws with SkiaSharp, `OfficeFixtures.cs` builds OOXML packages.

Per [CLAUDE.md](../../CLAUDE.md) the fixtures are **committed binaries**: run this only
when you have deliberately changed a fixture builder, never as a routine step.

## Run

```bash
# From the repo root -- the default output path is relative to your shell's cwd,
# not to the project directory.
dotnet run --project tools/make-golden                    # -> tests/DocInt.Tests/golden
dotnet run --project tools/make-golden -- /path/to/dir    # note the `--` separator
```

It creates the output directory if needed, writes 12 files, prints each one's size, and
ends with the resolved absolute path so you can confirm where they landed.

## Regenerating safely

A no-op run still rewrites 8 of the 12 fixtures, so overwriting in place makes
`git status` useless as a signal. Generate into a scratch directory and copy back only
what you meant to change:

```bash
dotnet run --project tools/make-golden -- /tmp/golden-new
for f in /tmp/golden-new/*; do
  cmp -s "$f" "tests/DocInt.Tests/golden/$(basename "$f")" || echo "DIFF $(basename "$f")"
done
cp /tmp/golden-new/bom.xlsx tests/DocInt.Tests/golden/    # just the one you edited
```

`git checkout -- tests/DocInt.Tests/golden` restores everything if you did overwrite.

What churns, and why:

- **DOCX / PPTX / XLSX** differ on every run from **ZIP entry timestamps alone** — the
  entry contents are byte-identical. To tell a real change from noise, compare `unzip -l`
  output or the extracted XML, not the container bytes.
- `text.pdf`, `sample.html`, `corrupt.xlsx`, `unknown.bin` reproduce byte for byte.
- `photo.png` and `scanned.pdf` are SkiaSharp renders and depend on the machine: the text
  is drawn with `SKTypeface.Default`, which resolves to whatever system font is available.
  They also move with the SkiaSharp version — as of 2026-08-03 a local regeneration
  produced a `photo.png` of 13,664 bytes against the committed 16,153, the committed copy
  most likely predating the SkiaSharp 4.151.0 pin.

## Verifying a regeneration

The enforced gate, from the repo root:

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

That is **not sufficient for the two image fixtures.** Offline, `photo.png` and
`scanned.pdf` are only checked for kind detection and non-emptiness; the tests that read
their visual content — `Photo_description_mentions_lens_or_uv_cues` and
`Scanned_pdf_proves_ocr` — live in the env-gated `LiveSmokeTests`. Because the drawn text
depends on a system-resolved typeface, a regeneration can silently produce a PNG in which
`UV400` is no longer legible while the offline suite stays green. After any change
touching `ImageFixtures.cs`, run the live suite too (see CLAUDE.md for the env vars):

```bash
DOCINT_LIVE_TESTS=1 dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~LiveSmokeTests"
```

## What each fixture pins

| Fixture | Pins |
| --- | --- |
| `text.pdf` | A PDF with a real text layer. Its body string is also the redaction test's probe — change it and `RedactionTests` must change with it. |
| `scanned.pdf` | An image-only page (a JPEG as a `/DCTDecode` XObject, **no text objects**) — the OCR proof. |
| `sample.docx` · `sample.pptx` · `sample.html` | The remaining layout-engine kinds. |
| `bom.xlsx` | Numeric fidelity: shared string, plain number, formula with cached value, boolean, date-styled OADate, plus a second sheet. |
| `chartsheet.xlsx` | A chartsheet tab resolves to a `ChartsheetPart`, not a `WorksheetPart` — must be skipped with a warning, not throw. |
| `overflow.xlsx` | Raw `<v>` of `1e400`: `decimal.Parse` overflows and the `double` fallback is `+Infinity`, which `System.Text.Json` refuses — so the cell must stay text. |
| `malformed-cells.xlsx` | Non-numeric number cell, unparseable `Date`-typed cell, date-styled serial outside `FromOADate` range — each with a sibling control cell proving the row survives. |
| `photo.png` | The vision engine. The drawn `UV400` text is what the live assertion reads. |
| `corrupt.xlsx` | ZIP magic followed by garbage — exercises the per-file `error` path inside a 200. |
| `unknown.bin` | Bytes with no detectable kind. |
