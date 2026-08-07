# 📄 EuGo-docint

Stateless, cluster-internal document-understanding service for the EuGo platform:
files in → Markdown, tables with typed numeric cells, image descriptions, per-file
warnings/errors out. Document-level language only — no compliance semantics, no storage,
no document content in logs.

- Design (authoritative): [docs/superpowers/specs/2026-07-19-eugo-docint-design.md](docs/superpowers/specs/2026-07-19-eugo-docint-design.md)
- Conventions & commands: [CLAUDE.md](CLAUDE.md)

## 🔌 API

`POST /v1/extract` — `multipart/form-data`: N file parts named `files` (pdf/docx/pptx/html/xlsx/jpg/png),
optional `hints` part `{"<filename>":{"purpose":"bom|photo"}}`. Well-formed requests always return
`200` with per-file success or error. Also: `GET /healthz`, `GET /info`, OpenAPI JSON in Development.

Limits (files per request, bytes per file, per-file timeout) are configurable — see
[Configuration](#-configuration). Wire format: camelCase, lowercase enum values, null fields omitted.

### Single PDF

Request example:

```bash
curl -s http://localhost:8090/v1/extract \
  -F "files=@invoice.pdf;type=application/pdf"
```

Response example:

```json
{
  "files": [
    {
      "name": "invoice.pdf",
      "kind": "pdf",
      "markdown": "# Invoice 2026-041\n\nSupplier: Acme GmbH\n…",
      "warnings": []
    }
  ]
}
```

### Mixed batch with hints

```bash
curl -s http://localhost:8090/v1/extract \
  -F "files=@bom.xlsx;type=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" \
  -F "files=@lens-photo.jpg;type=image/jpeg" \
  -F 'hints={"bom.xlsx":{"purpose":"bom"},"lens-photo.jpg":{"purpose":"photo"}}'
```

```json
{
  "files": [
    {
      "name": "bom.xlsx",
      "kind": "xlsx",
      "markdown": "## Sheet1\n\n| Part | Qty | Unit price |\n|---|---|---|\n| UV filter glass | 200 | 3.75 |\n",
      "tables": [
        {
          "name": "Sheet1",
          "markdown": "| Part | Qty | Unit price |\n|---|---|---|\n| UV filter glass | 200 | 3.75 |\n",
          "rows": [
            ["Part", "Qty", "Unit price"],
            ["UV filter glass", 200, 3.75]
          ]
        }
      ],
      "warnings": []
    },
    {
      "name": "lens-photo.jpg",
      "kind": "image",
      "imageDescription": "A close-up photograph of a cylindrical glass lens element with a violet-tinted coating.",
      "warnings": []
    }
  ]
}
```

`tables[].rows` carries typed cells (JSON numbers stay numbers, one table per sheet) — consumers
should read `rows`, not re-parse the Markdown. Image descriptions are factual observations only.

### Per-file failure inside a 200

A corrupt or unsupported file never fails the request; it gets its own `error` entry:

```json
{
  "files": [
    { "name": "report.docx", "kind": "docx", "markdown": "# Quarterly report\n…", "warnings": [] },
    {
      "name": "broken.pdf",
      "kind": "pdf",
      "warnings": [],
      "error": { "code": "corrupt", "message": "file could not be parsed as pdf" }
    }
  ]
}
```

Error codes: `unsupported_type` · `too_large` · `empty_file` · `corrupt` · `timeout` ·
`engine_error` · `engine_unconfigured`.

### Request-level 400

Only a malformed *request* (not a bad file) returns non-200 — no `files` parts, body not
multipart, too many files, oversized `hints` — as an RFC 7807 problem:

```json
{
  "title": "Malformed extract request",
  "detail": "request contains no file parts named 'files'",
  "status": 400
}
```

## ▶️ Run

```bash
dotnet run --project src/AppHost      # Aspire dashboard + telemetry (dev)
dotnet run --project src/DocInt.Api   # plain service on http://localhost:8090
```

Nothing has to be configured to boot. Azure engines activate when their endpoint is set;
unconfigured ones answer with per-file `engine_unconfigured` while every other kind works
normally. Endpoints and credentials go in user-secrets or environment variables —
see [Configuration](#-configuration).

## 🔧 Configuration

Three surfaces, one set of keys. `appsettings.json` ships the defaults, environment variables
override them, and the Helm chart does nothing but *emit* those environment variables. In env
form a `:` becomes `__` — `DocInt:MaxFileBytes` → `DocInt__MaxFileBytes`.

| Running as | Set values in |
| --- | --- |
| Local process / Aspire | `src/DocInt.Api/appsettings.json` for the non-secret knobs; **user-secrets** for endpoints and credentials — `dotnet user-secrets --project src/DocInt.Api set "DocumentIntelligence:ApiKey" "<key>"` |
| Docker container | `-e Section__Key=value` — the image already carries `appsettings.json`; never bake credentials into an image |
| Kubernetes | `charts/eugo-docint/values.yaml`, or `--set` at install time; credentials come from **Workload Identity**, never from values |

An empty chart value omits its environment variable and leaves the image's `appsettings.json`
default in force. That is why `values.yaml` carries no copy of the numbers below — one source of
truth, nothing to drift.

### Shipped defaults — `src/DocInt.Api/appsettings.json`

```jsonc
  "DocInt": {
    "MaxFileBytes": 52428800,
    "MaxFilesPerRequest": 32,
    "PerFileTimeoutSeconds": 100,
    "MaxParallelism": 4,
    // Boot-time reachability check over whichever endpoints below are set.
    "StartupProbe": {
      "Enabled": true,
      "Attempts": 3,
      "RetryDelaySeconds": 2,
      "AttemptTimeoutSeconds": 4,
      "TotalTimeoutSeconds": 25
    },
    // Periodic reachability check over the same endpoints; reported on /healthz, never fatal.
    "DependencyCheck": {
      "Enabled": true,
      "IntervalSeconds": 30,
      "TimeoutSeconds": 4
    }
  },
  // Both endpoints are blank by design — they are environment-specific and never committed.
  // Supply them via user-secrets or env; blank keeps the stub-first path, where that engine's
  // file kinds answer engine_unconfigured while the rest of the service works normally.
  "DocumentIntelligence": {
    // DocumentIntelligence__Endpoint, https://<resource>.cognitiveservices.azure.com/
    // Serves PDF/DOCX/PPTX/HTML via the built-in prebuilt-layout model — no deployment name.
    "Endpoint": ""
  },
  "AzureOpenAI": {
    // AzureOpenAI__Endpoint, https://<resource>.openai.azure.com/ — the resource root only;
    // the SDK appends /openai/deployments/<name>/chat/completions. Serves JPG/PNG.
    "Endpoint": "",
    // A deployment ALIAS, not a model name — deliberately decoupled from the model behind it
    // (EuGo-infra docs/naming-convention.md, model-<project>-<role>). The model can change on
    // the Foundry side without touching this file; do not "correct" it to the model's name.
    "DeploymentNameVision": "model-eugo-docint-vision"
  },
```

### Application settings

| Key | Default | Chart value | What it does |
| --- | --- | --- | --- |
| `DocInt:MaxFileBytes` | `52428800` (50 MB) | `docint.maxFileBytes` | Per-file size cap; a larger file gets its own `too_large` error inside a 200 |
| `DocInt:MaxFilesPerRequest` | `32` | `docint.maxFilesPerRequest` | Files accepted per request; more than this is a request-level 400 |
| `DocInt:PerFileTimeoutSeconds` | `100` | `docint.perFileTimeoutSeconds` | Per-file engine budget; exceeding it yields a per-file `timeout` |
| `DocInt:MaxParallelism` | `4` | `docint.maxParallelism` | Files processed concurrently. Raise it *with* `resources.limits.memory`, never alone — each in-flight file is buffered whole in memory |
| `DocInt:StartupProbe:Enabled` | `true` | `extraEnv` | Dial every configured endpoint once at boot and refuse to start if one stays unreachable. See [Startup connectivity check](#startup-connectivity-check) |
| `DocInt:StartupProbe:Attempts` | `3` | `extraEnv` | Attempts per endpoint, counting the first. Only a transport failure or a 408/429/5xx is retried |
| `DocInt:StartupProbe:RetryDelaySeconds` | `2` | `extraEnv` | Base backoff between attempts; exponential with jitter, capped at 2× the base |
| `DocInt:StartupProbe:AttemptTimeoutSeconds` | `4` | `extraEnv` | Ceiling on one attempt, so a hung handshake can't starve the rest. Must cover a cold `DefaultAzureCredential` token round-trip, not just the call |
| `DocInt:StartupProbe:TotalTimeoutSeconds` | `25` | `extraEnv` | Ceiling on the whole check. Must be ≥ `Attempts × AttemptTimeoutSeconds` (rejected at boot otherwise); past ~30 s the pod's liveness probe restarts the container mid-check |
| `DocInt:DependencyCheck:Enabled` | `true` | `extraEnv` | Re-dial every configured endpoint every `IntervalSeconds` and report it on `/healthz`. False registers neither the monitor nor the checks, so `/healthz` reports only `self`. See [What `/healthz` reports](#what-healthz-reports) |
| `DocInt:DependencyCheck:IntervalSeconds` | `30` | `extraEnv` | Seconds between rounds. At 30 s this is 2 calls/min per dependency per pod; for Azure OpenAI that is a one-token completion against the same quota real vision traffic uses |
| `DocInt:DependencyCheck:TimeoutSeconds` | `4` | `extraEnv` | Ceiling on one probe. Must be **less than** `IntervalSeconds` (rejected at boot otherwise), so a slow probe cannot overlap the next tick |
| `DocumentIntelligence:Endpoint` | `""` | `azure.documentIntelligence.endpoint` | `https://<resource>.cognitiveservices.azure.com/`. Serves PDF/DOCX/PPTX/HTML through the built-in `prebuilt-layout` model — no deployment name involved. Blank leaves those kinds on `engine_unconfigured` |
| `DocumentIntelligence:ApiKey` | *unset — not in `appsettings.json`* | none, by design | Omit it and the client uses `DefaultAzureCredential` |
| `AzureOpenAI:Endpoint` | `""` | `azure.openAI.endpoint` | `https://<resource>.openai.azure.com/` — the resource root only; the SDK appends `/openai/deployments/<name>/chat/completions`. Serves JPG/PNG |
| `AzureOpenAI:ApiKey` | *unset — not in `appsettings.json`* | none, by design | As above: absent means `DefaultAzureCredential` |
| `AzureOpenAI:DeploymentNameVision` | `model-eugo-docint-vision` | `azure.openAI.deploymentNameVision` | A deployment **alias**, not a model name — decoupled on purpose (EuGo-infra `docs/naming-convention.md`, `model-<project>-<role>`) so the model behind it can change without touching the service. Don't "correct" it to the model's name |
| `Serilog:MinimumLevel:Default` | `Information` (`Microsoft` and `System` at `Error`) | `extraEnv` | Log verbosity. Document *content* is never logged at any level |
| `Kestrel:EndPoints:Http:Url` | `http://*:8090` | — (chart fixes the container port at 8090) | Listen address |
| `ASPNETCORE_ENVIRONMENT` | `Production` in the container | `extraEnv` | `Development` additionally maps the OpenAPI document |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | `extraEnv` | When set, turns on both the OTel trace/metric exporter and the Serilog OTLP log sink. The Aspire AppHost sets it for you |

Both `ApiKey` keys are bound by the options classes but deliberately absent from the committed
`appsettings.json` — they exist only in user-secrets or the environment.

**Derived, not settable.** Kestrel's max request body is `MaxFileBytes × MaxFilesPerRequest + 1 MiB`
(≈1.6 GB at the defaults), so raising a limit raises it automatically — there is no separate
request-size knob.

**Validated at boot, not on first request.** All four `DocInt:*` values must be positive, both
endpoints must be absolute URIs, and `DeploymentNameVision` is required once `AzureOpenAI:Endpoint`
is set; a violation stops the host at startup. The service then logs its whole effective
configuration once, with secret-shaped keys valued `***redacted***` — the fastest way to confirm
what a pod actually came up with.

### Startup connectivity check

Valid configuration is not the same as reachable configuration. Before Kestrel binds, the service
dials each **configured** endpoint once — Document Intelligence via `GET /documentintelligence/info`,
Azure OpenAI via a one-token completion against `DeploymentNameVision` — and logs the result:

```
info: Connection to Document Intelligence at https://aif-eugo-swc.cognitiveservices.azure.com/ established on attempt 1 of 3 in 412 ms
fail: Connection to Azure OpenAI at https://aif-eugo-swc.openai.azure.com/ failed after 1 attempt(s) in 233 ms: HTTP 403: Public access is disabled.
```

Neither call sends document content, and the Azure OpenAI one costs a single token per pod start.

If an endpoint stays unreachable the host does not start and the process exits 1 — on AKS, a
CrashLoopBackOff whose logs name the endpoint and the status, instead of a healthy pod that turns
every PDF into a per-file `engine_error` only the caller ever sees. The rules:

- **A blank endpoint is skipped.** The stub-first deployment is still legal; only an endpoint
  someone asked for is one the service must be able to reach.
- **Three attempts, but only for transport failures and 408/429/5xx** — the blips a pod hits when
  its node's DNS or the Workload-Identity token endpoint is still warming up. A definitive status
  (401, 403, 404) means the service answered, and retrying a denied identity or a wrong deployment
  name cannot change the answer, so the check stops on the first one.
- **The Azure SDKs' own retry is disabled for the probe**, so 3 attempts means 3, not 9–16.

Turn it off with `DocInt:StartupProbe:Enabled=false` where the endpoints are unreachable **by
design** — the common case being a developer machine outside the VNet, since `aif-eugo-swc` has
`publicNetworkAccess: Disabled` and answers only through its private endpoint:

```powershell
$env:DocInt__StartupProbe__Enabled = 'false'; dotnet run --project src/DocInt.Api
```

```bash
DocInt__StartupProbe__Enabled=false dotnet run --project src/DocInt.Api
```

Note that `src/DocInt.Api/appsettings.Development.json` is untracked and typically carries the real
endpoints, so on a developer machine this is the difference between `dotnet run` starting and not.

**Credentials never go in `values.yaml`.** The chart has no `ApiKey` value on purpose: a key
routed through `extraEnv` would sit in plaintext in the release manifest. In-cluster the pod
authenticates with Workload Identity — set `serviceAccount.azureClientId` and leave the keys
unset. Note that `DefaultAzureCredential` in a container cannot fall back to the Azure CLI (the
chiseled image has no shell), so a container needs a real identity leg or an API key.

### What `/healthz` reports

`/healthz` is the readiness probe. It answers **200** with a JSON body:

```json
{
  "status": "Degraded",
  "checks": [
    { "name": "self", "status": "Healthy" },
    { "name": "Azure OpenAI", "status": "Degraded",
      "endpoint": "https://aif-eugo-swc.openai.azure.com/",
      "lastCheckedUtc": "2026-08-07T10:12:03.0000000Z",
      "reason": "HTTP 403: Public access is disabled." }
  ]
}
```

A background monitor re-dials each configured endpoint every
`DocInt:DependencyCheck:IntervalSeconds` and records the verdict; the endpoint reads that
record, so no request ever waits on Azure. Only configured endpoints appear — a stub-first
deployment shows just `self`. In the window between boot and the first round a dependency reads
`Degraded` with the reason `not yet checked`: a fresh pod never inherits a verdict it did not
make itself.

**A degraded dependency still returns 200, on purpose.** All replicas share the same Foundry
resource, so failing readiness would not shed load onto a healthy pod — it would empty the
Service and turn a partial outage into a total one, taking the Azure-free XLSX path down with
it. PDF and image requests keep returning their per-file `engine_error` inside a 200, which is
what Contract v1 promises. Only `Unhealthy` maps to 503, and nothing here produces it.

`/alive` is the liveness probe and is deliberately blind to all of this: it evaluates only
checks tagged `live`, and no dependency check carries that tag. A dependency outage must never
restart a pod that is serving correctly.

Because the startup connectivity check aborts the boot when a configured endpoint is
unreachable, a dependency can only ever show as `Degraded` here if it failed *after* a
successful start — which is exactly the gap this closes. With that check turned off, the
report is the only reachability signal there is.

### Chart-only settings

Deployment shape — no `appsettings.json` equivalent:

| Value | Default | What it does |
| --- | --- | --- |
| `image.repository` | — (**required**) | e.g. `<acr>.azurecr.io/eugo-docint` |
| `image.tag` | `""` → `.Chart.AppVersion` | `appVersion` is CI-stamped; override only for local images |
| `image.pullPolicy` | `IfNotPresent` | `Never` for a locally-loaded image |
| `serviceAccount.create` / `.name` | `true` / `""` → chart fullname | |
| `serviceAccount.azureClientId` | `""` | When set, adds the Workload-Identity annotation to the ServiceAccount and the `azure.workload.identity/use` label to pods |
| `autoscaling.enabled` · `minReplicas` · `maxReplicas` · `targetCPUUtilizationPercentage` | `true` · `2` · `6` · `70` | HPA |
| `replicaCount` | `2` | Used only when `autoscaling.enabled: false` |
| `resources` | requests `250m` / `512Mi`, limit `2Gi` memory | Memory is generous because files are buffered whole; no CPU limit, since throttling costs more latency than it saves |
| `service.port` | `8090` | ClusterIP port — no ingress, by design |
| `extraEnv` | `[]` | Verbatim `name`/`value` entries for keys with no first-class value above |

## 🚢 Deploy

Helm chart in [charts/eugo-docint](charts/eugo-docint) — Deployment, ClusterIP service on 8090,
Workload-Identity ServiceAccount, HPA (CPU 70 %, min 2 / max 6). No ingress: the service is
cluster-internal by design. Probes: liveness `/alive`, readiness `/healthz`.

```bash
helm install docint charts/eugo-docint \
  --set image.repository=<acr>.azurecr.io/eugo-docint \
  --set serviceAccount.azureClientId=<workload-identity-client-id> \
  --set azure.documentIntelligence.endpoint=https://<di>.cognitiveservices.azure.com/ \
  --set azure.openAI.endpoint=https://<aoai>.openai.azure.com/
# in-cluster URL: http://docint-eugo-docint.<namespace>.svc:8090/v1/extract
```

Versioning: chart and image share `major.minor`; the chart patch moves independently
(`chart-v*` tags release chart-only changes). CI stamps `appVersion` — never hand-edit it.
Tag `vX.Y.Z` → image + chart to ACR; tag `chart-vX.Y.P` → chart only. Cluster provisioning
(AKS, ACR, identity federation) stays in EuGo-infra.

**Release prerequisites** — before a tag push can publish, the GitHub repo needs four
*variables* (Settings → Secrets and variables → Actions → Variables; not secrets — auth is
OIDC, no stored credentials): `ACR_NAME` (registry name without `.azurecr.io`),
`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. The client ID must belong to
an Entra app/managed identity with a federated credential trusting this repo's GitHub Actions
OIDC tokens and `AcrPush` on the registry. The ACR itself is provisioned by EuGo-infra —
until it exists, don't push `v*`/`chart-v*` tags (the release run would just fail).

## 🧪 Test

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Live smoke against real Azure is env-gated — see CLAUDE.md. Container: `docker build -t eugo-docint .`
(`ci.yml` builds linux/amd64 to prove the Dockerfile; `release.yml` publishes linux/amd64 + linux/arm64). Cluster provisioning (AKS, ACR, identity) lives in the EuGo-infra repo; the deployment chart is in `charts/eugo-docint` (see Deploy).
