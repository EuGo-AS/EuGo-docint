# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

**EuGo-docint** (*EuGo Document Intelligence*) — an independent, **stateless, cluster-internal document-understanding service**: files in → Markdown, tables with typed numeric cells, image descriptions, per-file warnings/errors out. It renders no compliance judgment and stores nothing. It is called by EuGo-Web (and later EuGo-mcp) over `POST /v1/extract` on a shared AKS cluster, and is never exposed via ingress.

**Planning lives in `openspec/`.** All new work is proposed, specced, and tracked there — start with
`openspec list` to see active changes and `openspec new change "<name>"` to open one (never hand-create
a change directory; the CLI writes required metadata). Project context and the per-artifact rules that
bind every proposal, spec, design, and task list are in `openspec/config.yaml`.

The documents below are **historical records of work already shipped**. Read them for rationale — they
explain why things are the way they are, and the designs carry "deliberately deferred" ledgers worth
consulting before re-deriving an alternative. Do not update them, and do not add new ones:

- Service design as shipped for T1–T6: `docs/superpowers/specs/2026-07-19-eugo-docint-design.md`, alongside the later dated designs and plans under `docs/superpowers/`
- Plan (task level, T1–T8), in the sibling Obsidian vault: `../EuGo-Obsidian/plans/eugo-docint-plan.md`
- Spec (Decision 12): `../EuGo-Obsidian/plans/Plan-A-Step-1/12-decision-eugo-docint.md` — **amended 2026-07-19**: the "no Aspire AppHost" dev-loop note is superseded; the solution uses .NET Aspire (AppHost + ServiceDefaults) per user directive.

Sibling repos: `../EuGo-mcp` (conventions to mirror — its `CLAUDE.md` is the reference), `../EuGo-web` (the consumer), `../EuGo-infra`.

**Tech stack:** .NET 10 · ASP.NET Core minimal API · `Azure.AI.DocumentIntelligence` (Layout → Markdown) · OpenXML (XLSX typed cells) · Azure OpenAI Foundry utility model (image description) · OTel + Serilog · Docker → ACR → AKS.

## Commands

The solution will live at `src/DocInt.slnx` (not the repo root) — always target it explicitly:

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

That exact three-step sequence, in that order, is the enforced gate before any merge — `--no-restore` / `--no-build` keep the signal honest (a test failure is a test failure, not a stale build).

Run a single test: `dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~TestName"`.

Run the app two ways: `dotnet run --project src/AppHost` (Aspire dashboard + OTLP telemetry, preferred for dev) or `dotnet run --project src/DocInt.Api` directly on `http://localhost:8090` (8089 belongs to the siblings). Credentials via user-secrets/env locally (endpoint without key → `DefaultAzureCredential`), Workload Identity on AKS. Config keys: `DocumentIntelligence:*`, `AzureOpenAI:*`, `DocInt:*` — see the design doc's table.

## Architecture

Planned layout (locked by the plan — keep the decomposition):

```
src/DocInt.slnx
├─ DocInt.Api/          ASP.NET Core minimal API (net10.0)
│  ├─ Contracts/        request/response DTOs + OpenAPI
│  ├─ Engines/          EngineRouter · LayoutEngine · SpreadsheetEngine · VisionEngine
│  ├─ Validation/       size/type caps, kind detection
│  └─ Telemetry/        Serilog config, the EuGo.DocInt meter (7 instruments), duplicate tracker
├─ AppHost/             Aspire orchestrator (Aspire.AppHost.Sdk/13.1.0), resource "docint"
└─ ServiceDefaults/     stock Aspire defaults (OTel, health, resilience)
tests/DocInt.Tests/     contract + golden-file tests (env-gated live smoke)
└─ golden/              12 committed binaries: text PDF · scanned PDF · DOCX · PPTX · HTML · BoM XLSX · 3 XLSX edge cases · photo · corrupt · unknown bytes
tools/make-golden/      generator for the golden fixtures (see its README)
Dockerfile              chiseled aspnet, multi-arch (amd64/arm64), mirrors EuGo-mcp's (port 8090)
charts/eugo-docint/     Helm chart: Deployment, ClusterIP Service, WI ServiceAccount, HPA
```

The Helm chart lives in `charts/eugo-docint` (per the 2026-07-26 design spec, superseding the earlier "no K8s manifests here" note). EuGo-infra still owns cluster provisioning (AKS, ACR, Workload Identity federation) and release execution. Chart checks: `helm lint charts/eugo-docint` and `helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml`; CI's `chart-lint` also renders from minimal values and asserts the pod mounts a writable `/tmp`. **That mount is load-bearing, not cosmetic** — the pod runs `readOnlyRootFilesystem: true`, and OpenXML spills package parts to `/tmp` even though `SpreadsheetEngine` reads from a `MemoryStream`, so removing it breaks *every* XLSX request on every deployment (it did, until 2026-08-06). Render/lint cannot catch that class of bug: the read-only root comes only from the chart, so the golden tests and the Docker build both pass on a writable filesystem — changes to the security context need a real pod (`kind` + `helm install` + a BoM XLSX through `/v1/extract`). Versioning: chart `major.minor` always equals the image's; chart `patch` is chart-owned; `appVersion` is CI-stamped at package time — never hand-edit it. Release tags: `vX.Y.Z` = image + chart, `chart-vX.Y.P` = chart only.

**Contract v1** (frozen at T2; `/v1` is internal-may-change until EuGo-mcp becomes the second consumer):
`POST /v1/extract` — multipart, N files + per-file hints (filename, content type, optional purpose hint e.g. `bom`, `photo`) → synchronous
`200 { files: [ { name, kind, markdown?, tables?, imageDescription?, warnings[], error? } ] }` with `kind ∈ pdf | docx | pptx | html | xlsx | image`.

**Engines** — all implement the internal seam `IExtractionEngine`, kind-keyed behind `EngineRouter`:

| Kind | Engine |
| --- | --- |
| PDF (incl. scanned), DOCX, PPTX, HTML | DI `prebuilt-layout` → Markdown |
| XLSX | OpenXML typed cells + Markdown rendering (`tables` carries the typed rows — numeric fidelity is the point) |
| JPG/PNG | Vision description via Foundry utility model — **factual observations only**, no classification language |

**Task order:** T1 scaffold → T2 contract/stubs (freezes the wire contract), then T3 layout · T4 spreadsheet · T5 vision in parallel; T6 hardening; T7 AKS deploy (blocked on infra). EuGo-Web integration (T8) proceeds against T2's stubs and lives in the EuGo-Web plan.

## Hard constraints (from the spec — apply to every change)

- **Facts, never meaning.** Document-level language only: no `EvidenceBundle`, no categories, no compliance/classification semantics anywhere in the API or code.
- **Stateless & storage-free.** Bytes in, results out; no document persistence; docint must never grow into a document store.
- **No document content in logs** — filenames and sizes are fine, content never. Serilog gets a content-redaction rule (T6).
- **Per-file success/failure inside a 200.** One corrupt file yields its own `error` entry, never a failed call; request-level errors only for malformed requests (400).
- **EU-region Azure resources only**; Workload Identity on AKS; never secrets in source or tracked config.

## Conventions (mirror EuGo-mcp)

- **Workflow per task:** cut a per-step branch → TDD (failing test first) → `restore` → `build --no-restore` → `test --no-build` → merge to `main` once green → delete the branch. Never develop directly on `main`; never merge red.
- The `.claude/agents/dotnet-developer.md` agent encodes the full PR-based variant (four-section PR bodies, `agent/<slug>` branches); use it for dispatched tasks. When the plan's lighter merge-to-main flow and the agent's never-merge rule conflict, the plan's flow wins for routine steps unless the user asks for PRs.
- Don't skip or weaken tests over missing infrastructure (no Azure credentials, no Docker) — run the subset that can execute and list what was blocked. Live-smoke tests are env-gated by design.
- Testing is golden-file driven: contract tests assert response shapes with Azure stubbed; the SpreadsheetEngine golden test proves BoM XLSX numeric fidelity offline (OpenXML, no Azure); the env-gated live smoke suite proves OCR (scanned PDF) and lens/UV cues on the photo against real Azure.
- `net10.0`, `Nullable` and `ImplicitUsings` enabled across projects.
- **Use the Context7 MCP server for documentation lookups** (Azure.AI.DocumentIntelligence, OpenXML, Azure OpenAI, ASP.NET Core) rather than memory — version-accurate docs matter here.

## Live smoke tests

Azure-stubbed tests are the default; the live suite (`LiveSmokeTests`) self-skips unless gated on:

```bash
export DOCINT_LIVE_TESTS=1
export Foundry__DocumentIntelligenceEndpoint=https://<resource>.cognitiveservices.azure.com/
export Foundry__OpenAIEndpoint=https://<resource>.openai.azure.com/
export Foundry__ApiKey=<key>   # ONE key for both — omit to use DefaultAzureCredential (az login)
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~LiveSmokeTests"
```

**Disconnect any corporate VPN client first.** Exporting the variables is not sufficient: a client
that pins DNS to its own resolver (GlobalProtect, measured 2026-08-19) forges NXDOMAIN for every
query these tests need, and the suite fails or self-skips with nothing wrong at either end. See the
third trap under *Network reachability* for the one-line check.

**One key, two hosts.** Both endpoints are surfaces of the single Foundry account `aif-eugo-swc`
(`kind: AIServices`), which exposes one key pair — `key1`/`key2` rotate, they are not one key per
API. `Foundry__ApiKey` authenticates both.

**Use a shell with no leftovers.** `DocumentIntelligence__*` and `AzureOpenAI__*` are retired: if
one still carries a value the host **refuses to start**, naming the replacement. That is by design
— an unbound key would be ignored, and absent already means `DefaultAzureCredential`, so a stale
value would silently switch one surface to a different credential. Two consequences worth knowing:
the failure hits **every** test that builds the app, not just the live ones; and the live tests'
skip gates read `Foundry__*` from the environment directly, so a stale export leaves them reporting
SKIPPED rather than failing.

**Network reachability (verified 2026-08-07).** The provisioned EuGo Foundry resource
`aif-eugo-swc` (Sweden Central) has `publicNetworkAccess: Disabled` — it answers only through
its private endpoint. A call from an unconnected machine returns `403 "Public access is
disabled."`, so the commands above need a path **inside** the VNet. There are three:

1. **The Tailscale subnet router** — the normal developer path, and it does work from a
   workstation. Start the VM (`az vm start -g rg-eugo-net-swc -n vm-eugo-vpn-swc`; it is
   deallocated by default), join the tailnet, and the endpoints resolve to their private
   addresses. Requires the route `10.60.0.0/16` approved and **Split DNS registered on the
   public suffixes** `cognitiveservices.azure.com` / `openai.azure.com` — *not* the
   `privatelink.*` forms, which silently never match. Setup and the failure signatures are in
   `../EuGo-infra/docs/net.md` § *Developer access* and § *Split DNS binds to the public suffix*.
2. **From inside the cluster** — a pod or jumpbox, no tunnel needed.
3. **A separate public dev resource** — point the endpoints elsewhere entirely.

Confirm with `Resolve-DnsName aif-eugo-swc.cognitiveservices.azure.com` (Windows) or `dig`;
a `10.60.5.x` answer means you are connected, a public IP means you are not. Two traps on
Windows: use `Resolve-DnsName`, **never `nslookup`** — the latter bypasses the NRPT rules
Tailscale installs and reports the public address even when the tunnel is working; and run
`dotnet test` **from Windows, not WSL** — WSL2 under NAT networking inherits neither the tailnet
route nor the NRPT rules, so the endpoints will not resolve there.

**A third trap, and the worst of them (found 2026-08-19): a second VPN client can intercept every
UDP DNS query and forge the answer.** On a workstation running Tailscale alongside a corporate
client (GlobalProtect was the one observed), queries to any resolver other than the corporate one
are answered locally with NXDOMAIN — including the queries Tailscale's own resolver makes to the
subnet router, so Split DNS silently never works. It looks exactly like a broken router, and it
cost two wrong diagnoses before being caught. `Resolve-DnsName` cannot see through it: the forged
answer is well-formed, arrives instantly, and carries a spoofed source address.

The one-line discriminator, before blaming anything on the far end:

```powershell
Resolve-DnsName google.com -Server 192.0.2.1    # TEST-NET; must NOT answer
```

`192.0.2.1` is reserved by RFC 5737 and cannot host a resolver. **Any** reply proves local
interception, and while it is active no `10.60.5.x` answer is reachable no matter how healthy the
tunnel is — check this before touching the router or the tailnet config. Non-DNS traffic is
unaffected, so `Test-NetConnection 10.60.5.4 -Port 443` still succeeds and is the honest test of
whether the route works. Workarounds: disconnect the intercepting client, or map the two Foundry
hostnames to `10.60.5.4` / `10.60.5.5` in `hosts` (the certificate SANs are the public names, so
TLS still verifies) — the latter is temporary, since those addresses change if the private
endpoints are recreated.

Both endpoints share the one resource, on two hostnames:
`https://aif-eugo-swc.cognitiveservices.azure.com/` for Document Intelligence and
`https://aif-eugo-swc.openai.azure.com/` for Azure OpenAI. See "Azure resource shape" in
`docs/superpowers/specs/2026-07-19-eugo-docint-design.md`.

Golden fixtures are committed binaries; regenerate only deliberately with `dotnet run --project tools/make-golden`, **from the repo root** (the default output path is cwd-relative). See `tools/make-golden/README.md` for the regenerate-to-a-scratch-dir workflow, why a no-op run still rewrites most fixtures, and why a change touching `ImageFixtures.cs` must be verified against the live suite rather than the offline one.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
