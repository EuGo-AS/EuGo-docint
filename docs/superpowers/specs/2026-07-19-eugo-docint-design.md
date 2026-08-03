# EuGo-docint — service design (T1–T6)

* **Status:** Approved 2026-07-19 (in-session brainstorm)
* **Upstream spec:** `EuGo-Obsidian/plans/Plan-A-Step-1/12-decision-eugo-docint.md` (Decision 12) · task plan `EuGo-Obsidian/plans/eugo-docint-plan.md`
* **Scope of this design:** T1–T6 — scaffold, frozen `/v1` contract, all three engines, hardening. **Out of scope:** `deploy/` K8s manifests (owned by EuGo-infra), EuGo-Web integration (T8, lives in EuGo-web).
* **Amendment to Decision 12:** the "no Aspire AppHost of its own" dev-loop note is superseded by user directive (2026-07-19): the solution **uses .NET Aspire** — AppHost + ServiceDefaults, mirroring EuGo-mcp and EuGo-web.
* **Azure availability:** DI + AOAI resources are being provisioned in parallel (EuGo-infra). Everything here works stub-first; live smoke is env-gated and lights up when endpoints/keys arrive. No code changes required at that point.

## What the service is

A stateless, cluster-internal document-understanding service: files in → Markdown, tables with typed cells, image descriptions, per-file warnings/errors out. Document-level language only — no compliance semantics, no `EvidenceBundle`, no categories. Stores nothing; no document content in logs.

## Solution structure

```text
src/DocInt.slnx
├─ DocInt.Api/          ASP.NET Core minimal API (net10.0)
│  ├─ Contracts/        request/response DTOs + OpenAPI
│  ├─ Engines/          EngineRouter · LayoutEngine · SpreadsheetEngine · VisionEngine
│  ├─ Validation/       multipart caps, kind detection
│  └─ Telemetry/        Serilog config, pages-processed metric
├─ AppHost/             Aspire orchestrator (Aspire.AppHost.Sdk/13.1.0), resource "docint"
└─ ServiceDefaults/     stock Aspire defaults (OTel traces/metrics, health, resilience)
tests/DocInt.Tests/     xUnit; contract + golden + env-gated live smoke
└─ golden/              committed binary fixtures (see Testing)
tools/make-golden/      one-off generator for the golden fixtures
Dockerfile              chiseled aspnet, multi-arch (amd64/arm64), mirrors EuGo-mcp's
.github/workflows/ci.yml   build+test · docker (amd64, push: false) · chart-lint
.github/workflows/release.yml  v* / chart-v* tags: image + chart to ACR
```

All projects `net10.0`, `Nullable` + `ImplicitUsings` enabled.

**Run modes:** `dotnet run --project src/AppHost` (dashboard, OTLP) or `dotnet run --project src/DocInt.Api` directly on `http://localhost:8090` (8089 is taken by the siblings). Build/test gate: `dotnet restore src/DocInt.slnx` → `dotnet build --no-restore src/DocInt.slnx` → `dotnet test --no-build src/DocInt.slnx`.

**Logging:** Serilog (`ClearProviders` + `AddSerilog`, configured from appsettings), OTLP sink ships logs to the Aspire dashboard when `OTEL_EXPORTER_OTLP_ENDPOINT` is present; ServiceDefaults' OTel traces/metrics stay active (EuGo-mcp pattern).

**Endpoints:** `POST /v1/extract` · `GET /healthz` · `GET /info` (name, version, endpoint list) · OpenAPI JSON (Development) · ServiceDefaults `/health` + `/alive` (Development).

## Azure access & configuration

Engines never touch Azure SDKs directly. Two thin adapter interfaces are both the test seam and the credential seam:

* `ILayoutAnalysisClient` — wraps `Azure.AI.DocumentIntelligence` (`prebuilt-layout`, Markdown output).
* `IVisionChatClient` — wraps Azure OpenAI chat completions (image content part).

Credentials per client: endpoint + `ApiKey` configured → key credential; endpoint without key → `DefaultAzureCredential` (Workload Identity on AKS later; `az login`/user-secrets locally). Never secrets in source or tracked config.

**Lazy-required config:** the service boots and serves `/healthz` with no Azure config. A file whose engine lacks configuration gets a per-file `engine_unconfigured` error. This is what makes the stub-first period workable.

| Key | Default | Meaning |
| --- | --- | --- |
| `DocumentIntelligence:Endpoint`, `:ApiKey` | — | DI resource; key optional |
| `AzureOpenAI:Endpoint`, `:ApiKey` | — | AOAI/Foundry resource; key optional |
| `AzureOpenAI:DeploymentNameVision` | `gpt-5.4-mini` | vision deployment |
| `DocInt:MaxFileBytes` | 52 428 800 (50 MB) | per-file cap |
| `DocInt:MaxFilesPerRequest` | 32 | request cap |
| `DocInt:PerFileTimeoutSeconds` | 100 | per-file engine timeout |
| `DocInt:MaxParallelism` | 4 | files processed concurrently |

These defaults live **only** in `src/DocInt.Api/appsettings.json` — neither `DocIntOptions` nor `AzureOpenAIOptions` carries a value-bearing property initializer. An omitted or non-positive `DocInt:*` value therefore fails `ValidateOnStart` at boot rather than falling back to a second set of defaults hidden in code. `AzureOpenAI:DeploymentNameVision` is required only once `AzureOpenAI:Endpoint` is set — blank-with-no-endpoint stays legal, since that is the stub-first degraded mode above.

## Wire contract v1 (frozen; `/v1` is internal-may-change until EuGo-mcp is the second consumer)

### Request

`POST /v1/extract`, `multipart/form-data`:

* N file parts, field name `files`, each with filename + content type.
* Optional single part `hints`: JSON object keyed by filename: `{ "bom.xlsx": { "purpose": "bom" } }`.

**Purpose-hint taxonomy v1** (closes Decision 12 open item 2): `bom`, `photo`. Hints are advisory metadata in v1: validated; unknown value → per-file warning `unknown purpose hint '<x>' ignored`; no routing change (routing is kind-based). Room to make hints behavioral later without a contract break.

### Kind detection

Magic bytes are authoritative where they exist: `%PDF`, PNG/JPEG signatures, `PK` zip container. Zip containers disambiguate to docx/xlsx/pptx via content type/extension; HTML by content type/extension. Claimed type contradicts bytes → trust the bytes + warning. Undetectable → per-file `unsupported_type`.

### Response

Always `200` for a well-formed request; per-file success/failure inside the envelope:

```jsonc
{ "files": [ {
    "name": "manual.pdf",
    "kind": "pdf",                  // pdf | docx | pptx | html | xlsx | image (lowercase strings)
    "markdown": "...",              // DI kinds and xlsx
    "tables": [ {                   // xlsx only
      "name": "Sheet1",
      "markdown": "| ... |",
      "rows": [ [ "Part", "Qty" ], [ "M3 screw", 40 ] ]
    } ],
    "imageDescription": "...",      // image only
    "warnings": [],
    "error": null                   // or { "code": "...", "message": "..." }
} ] }
```

* Typed cells: JSON `number | string | boolean | null`; dates → ISO-8601 strings. Numeric cells carry the stored cell value (never re-parsed display text); formula cells use the cached computed value (missing → `null` + warning). This is the numeric-fidelity guarantee that justifies the engine.
* Results return in request order. Duplicate filenames are allowed: each part yields its own `FileResult` in order, and a `hints` entry for that name applies to every match.

### Errors

Per-file codes: `unsupported_type` · `too_large` · `empty_file` · `corrupt` · `timeout` · `engine_error` · `engine_unconfigured`.

Request-level `400` (ProblemDetails) only for: not multipart · zero files · more than `MaxFilesPerRequest` · malformed `hints` JSON · `hints` part over its read cap · declared request body over `MaxRequestBytes`. The last two are the request-scoped half of the streaming-caps rule below: `hints` is not file-scoped, so it has no per-file slot to carry an error. One corrupt file never fails the batch; there is no path to a 500 for bad input.

## Engines

```csharp
interface IExtractionEngine {
    IReadOnlySet<FileKind> Kinds { get; }
    Task<FileResult> ExtractAsync(FileWork work, CancellationToken ct);
}
```

`EngineRouter` selects by detected kind and owns: per-file timeout (`timeout` error) and catch-all (`engine_error`; exception message only, never content).

* **LayoutEngine** — pdf (incl. scanned), docx, pptx, html → DI `prebuilt-layout`, Markdown output. DI warnings map into `warnings[]`; DI page count feeds the metric.
* **SpreadsheetEngine** — xlsx via OpenXML SDK (no Azure). Per sheet: typed rows + Markdown rendering of the same grid. Shared strings resolved; numbers as `decimal`; booleans; dates → ISO-8601. Empty trailing rows/columns trimmed. No row truncation in v1 (the size cap is the guard).
* **VisionEngine** — jpg/png → one chat completion, image as data-URI content part. System prompt pinned as a code constant, snapshot-tested (closes Decision 12 open item 3):

> You describe product photographs for a document record. List only what is directly visible: objects, text, markings, labels, symbols, materials, colors, quantities. Transcribe visible text and codes exactly as printed. Do not identify product categories, do not infer purpose, compliance status, quality, or anything not visible in the image. Output a plain-text description.

## Cross-cutting

* **Parallelism:** `Parallel.ForEachAsync` over files, capped at `DocInt:MaxParallelism`; results re-assembled in request order.
* **Streaming limits:** multipart section caps enforced during read (a too-large part fails at the cap, not after buffering).
* **Tracing:** one activity per request, one per file (tags: kind, size, outcome — no filenames in trace tags; filenames may appear in logs).
* **Metric:** `docint.pages_processed` counter, tag `kind` — DI page count; xlsx = sheet count; image = 1.
* **Log redaction rule:** log filename, size, kind, duration, outcome, error code — never document content, never extraction output. Enforced by a log-capture test (process golden files, assert known content strings absent from captured logs).
* **Statelessness:** request-scoped byte handling only; nothing written to disk or any store.

## Testing

**Host:** `DocIntAppFactory` — `WebApplicationFactory` over the real `Program`, fake `ILayoutAnalysisClient`/`IVisionChatClient` injected; the HTTP pipeline, validation, router, and SpreadsheetEngine run for real.

**Golden fixtures** (committed binaries in `tests/DocInt.Tests/golden/`, generated once by `tools/make-golden`): text PDF · scanned PDF (image-only page, the OCR proof) · DOCX · PPTX · HTML · BoM XLSX · product photo (synthetic sunglasses image with visible "UV400" text) · corrupt file.

**Layers:**

1. Unit: kind detection, validation caps, hints parsing.
2. HTTP contract (fakes): response shape per kind, mixed good+corrupt batch, hint warnings, every 400 case, `engine_unconfigured` path.
3. SpreadsheetEngine golden tests (real engine): cell-by-cell numeric fidelity against the BoM XLSX.
4. **Live smoke** (env-gated: `DOCINT_LIVE_TESTS=1` + endpoints configured; skipped otherwise): every golden file through real Azure — non-empty Markdown per DI kind, scanned PDF yields OCR text, photo description mentions lens/UV cues, snapshot pins the vision output shape.

## CI & delivery

GitHub Actions (`EuGo-AS/EuGo-docint`), `.github/workflows/ci.yml`, on push/PR to `main` — three jobs. **build-test** and **chart-lint** start in parallel; only **docker** has a `needs:` (on build-test):

1. **build-test** — restore → build `--no-restore` → test `--no-build` against `src/DocInt.slnx` (live-smoke tests self-skip: no `DOCINT_LIVE_TESTS` in CI).
2. **docker** — image build via `docker/setup-qemu-action` + `docker/setup-buildx-action` + `docker/build-push-action`, `push: false`; build-only validation. **Amended 2026-08-03:** CI builds `linux/amd64` only. The multi-arch directive of 2026-07-19 (`linux/amd64` + `linux/arm64`) now lives in `release.yml`, which is where the image actually ships — CI's job is to prove the Dockerfile still builds, and the cross-publish pattern below means a second target arch mostly re-runs restore/publish for little extra signal.
3. **chart-lint** — `helm lint` plus two `helm template` renders of `charts/eugo-docint` (added by the 2026-07-26 Helm chart work). Publishes nothing.

**Amended 2026-08-03:** "pushing to ACR is EuGo-infra's CD job" no longer holds. Since the 2026-07-26 Helm chart work this repo publishes image *and* chart to ACR itself, from `.github/workflows/release.yml` on `v*` / `chart-v*` tags — see `docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md`. EuGo-infra still owns cluster provisioning and release execution.

**Dockerfile** (chiseled aspnet, port 8090) uses the standard .NET cross-publish pattern so ARM builds don't crawl under QEMU emulation: the SDK stage is pinned to the build host (`FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build`) and cross-targets via `ARG TARGETARCH` + `dotnet publish -a $TARGETARCH`; only the runtime stage (`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra`, itself multi-arch) resolves per target platform. Otherwise mirrors EuGo-mcp's Dockerfile (stage layout, `USER $APP_UID`, `UseAppHost=false`).

## Implementation workflow

Per-step branches ≈ T1–T6 (scaffold → contract+stubs → layout → spreadsheet → vision → hardening), TDD, the three-step gate, merge to `main` when green, delete the branch. T3/T4/T5 are independent once T2 freezes the contract.

## Resolved open items (from Decision 12)

1. AKS/ACR/Workload Identity infra → owned by EuGo-infra (parallel effort); this repo ships code + Dockerfile only.
2. Purpose-hint taxonomy → `bom` | `photo`, advisory in v1 (above).
3. Image-description prompt → pinned wording (above), snapshot-tested.
