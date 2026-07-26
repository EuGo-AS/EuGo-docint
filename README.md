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

Limits (configurable via `DocInt:*`): 32 files per request, 50 MB per file, 100 s per-file timeout.
Wire format: camelCase, lowercase enum values, null fields omitted.

### Single PDF

```bash
curl -s http://localhost:8090/v1/extract \
  -F "files=@invoice.pdf;type=application/pdf"
```

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

Azure engines activate when configured (user-secrets or env; endpoint without key → DefaultAzureCredential):
`DocumentIntelligence:Endpoint`, `AzureOpenAI:Endpoint`, `AzureOpenAI:DeploymentNameVision` (default `gpt-4.1-mini`).
Unconfigured engines answer with per-file `engine_unconfigured` — the service always boots.

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

## 🧪 Test

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Live smoke against real Azure is env-gated — see CLAUDE.md. Container: `docker build -t eugo-docint .`
(CI builds linux/amd64 + linux/arm64). Kubernetes deployment lives in the EuGo-infra repo.
