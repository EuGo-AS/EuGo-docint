# EuGo-docint Helm chart — design

**Date:** 2026-07-26
**Status:** approved in brainstorming; this document is the written spec.
**Supersedes:** the "K8s manifests are not in this repo — deployment is owned by EuGo-infra"
note in `CLAUDE.md`, for the Helm chart only. EuGo-infra still owns cluster provisioning
(AKS, ACR, Workload Identity federation) and release execution; this repo now owns the
chart that describes how docint runs.

**Amended 2026-08-03:** §7 and §8 track the implemented workflows — `ci.yml`'s chart job is
named `chart-lint` (it publishes nothing), and `release.yml` renders the packaged `.tgz`
between `helm package` and `helm push`. The dated plan under `docs/superpowers/plans/` is an
execution record and is deliberately left at its original wording.

## Goals

- A Helm chart at `charts/eugo-docint` that deploys the service to AKS (target
  Kubernetes 1.35): Deployment, ClusterIP Service on 8090, ServiceAccount with Azure
  Workload Identity wiring, HPA. **No ingress** — the service stays cluster-internal.
- Health endpoints in the API suitable for split Kubernetes probes.
- CI publishes a chart release for every Docker image release; chart and image share
  `major.minor`, chart `patch` moves independently.

## Non-goals

- No ingress, PDB, NetworkPolicy, or chart dependencies (YAGNI — single cluster-internal
  consumer).
- No API-key secret support in the chart — AKS auth is Workload Identity
  (endpoint-without-key → `DefaultAzureCredential`); no secrets in values, ever.
- No K8s manifests beyond the chart; no deployment automation to a live cluster (release
  execution stays with EuGo-infra).

## 1. API change: `/alive` liveness endpoint

`Program.cs` maps, unconditionally, next to the existing `/healthz`:

```csharp
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
```

- The `"self"` check registered by ServiceDefaults is tagged `live`, so `/alive` is
  immediately green and stays dependency-free by construction: future dependency checks
  (Azure reachability etc.) are added untagged, affecting `/healthz` (readiness) but never
  `/alive` (liveness).
- `/alive` is added to the `/info` endpoints list.
- The `app.MapDefaultEndpoints()` call in `Program.cs` is **removed**: it maps only
  Development-only `/health` and `/alive` endpoints, and keeping it would register
  `/alive` twice in Development (an `AmbiguousMatchException` at request time). The
  unconditional `/healthz` + `/alive` pair fully replaces it in every environment.
  `ServiceDefaults/Extensions.cs` itself stays stock and untouched.
- TDD: failing contract test in `DocInt.Tests` first (200 + `Healthy` on `/alive`,
  mirroring the existing `/healthz` test style), then the mapping.

Probe split rationale: a slow or unreachable Azure dependency must flip readiness (stop
routing traffic), never liveness (restart the pod).

## 2. Chart layout

```
charts/eugo-docint/
├─ Chart.yaml           # apiVersion v2; kubeVersion ">=1.30.0-0" documents the 1.35 target
├─ values.yaml
├─ .helmignore
├─ ci/
│  └─ test-values.yaml  # sample values for offline helm template smoke rendering
└─ templates/
   ├─ _helpers.tpl      # fullname, chart label, app.kubernetes.io/* label sets
   ├─ deployment.yaml
   ├─ service.yaml
   ├─ serviceaccount.yaml
   ├─ hpa.yaml          # autoscaling/v2
   └─ NOTES.txt         # prints in-cluster URL http://<fullname>.<ns>.svc:8090/v1/extract
```

Only long-stable APIs (`apps/v1`, `v1`, `autoscaling/v2`) — nothing 1.35-specific is
needed; `kubeVersion` is documentation, not a feature gate.

## 3. values.yaml

```yaml
image:
  repository: ""            # required at install, e.g. <acr>.azurecr.io/eugo-docint
  tag: ""                   # empty → .Chart.AppVersion
  pullPolicy: IfNotPresent

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 6
  targetCPUUtilizationPercentage: 70
replicaCount: 2             # used only when autoscaling.enabled=false

serviceAccount:
  create: true
  name: ""                  # empty → chart fullname
  azureClientId: ""         # when set: azure.workload.identity/client-id annotation on the
                            # SA and azure.workload.identity/use: "true" label on pods

azure:
  documentIntelligence:
    endpoint: ""            # → env DocumentIntelligence__Endpoint
  openAI:
    endpoint: ""            # → env AzureOpenAI__Endpoint
    deploymentNameVision: "" # empty → the image's appsettings.json (gpt-5.4-mini)

docint:                     # each empty → the image's appsettings.json default; set one to
  maxFileBytes: ""          # override it via the matching DocInt__* env var. Values are
  maxFilesPerRequest: ""    # rendered with int64 — a bare YAML number reaches Helm as a
  perFileTimeoutSeconds: "" # float64 and would otherwise emit 1.048576e+07.
  maxParallelism: ""

extraEnv: []                # verbatim env entries, for keys with no first-class value above

resources:
  requests: { cpu: 250m, memory: 512Mi }
  limits: { memory: 2Gi }   # no CPU limit — throttling hurts latency more than it helps

service:
  port: 8090
```

- Empty Azure endpoints omit the env var entirely; the service boots and answers
  `engine_unconfigured` per file — its designed degraded mode. No value is required for
  the chart to render except `image.repository`.
- The same omit-when-empty rule carries the `docint:` limits, so the chart never restates
  the shipped numbers — `appsettings.json` stays their single source of truth. A
  non-positive override fails `ValidateOnStart` at pod start rather than silently
  falling back.
- Port 8090 is baked into the image's `appsettings.json` (Kestrel endpoint) — the chart
  sets no port env var; `service.port` only controls the Service's exposed port.
- Memory limit is deliberately generous: requests buffer fully in memory (≤50 MB/file,
  `MaxParallelism` 4 by default).
- No `imagePullSecrets` — AKS pulls from ACR via the kubelet's managed identity.

## 4. Deployment template

- **Probes:** liveness `GET /alive` (period 10 s, failureThreshold 3), readiness
  `GET /healthz` (initialDelay 3 s, period 10 s, failureThreshold 3). No startup probe —
  minimal-API cold start is ~1 s.
- **Replicas:** the `replicas:` field is rendered only when `autoscaling.enabled=false`
  (from `replicaCount`); when the HPA owns the count, the field is omitted so
  `helm upgrade` never fights the autoscaler.
- **Security context:** `runAsNonRoot: true` (image runs as `$APP_UID` on chiseled
  aspnet), `readOnlyRootFilesystem: true`, `allowPrivilegeEscalation: false`,
  capabilities drop `ALL`, seccomp `RuntimeDefault`.
- **Writable `/tmp`** (added 2026-08-06): a disk-backed `emptyDir` mounted at `/tmp`.
  **Amended 2026-08-06:** the original rationale for `readOnlyRootFilesystem` — "the
  service never touches disk by design" — was wrong, and the chart shipped with no
  writable volume because of it. The service persists nothing, but *persisting nothing*
  is not *writing nothing*: `SpreadsheetEngine` opens the workbook from a `MemoryStream`,
  yet OpenXML / `System.IO.Packaging` spills package parts to a temp file underneath.
  With a read-only root and no `/tmp`, **every XLSX request on a chart-based deployment
  failed** with `engine_error "Read-only file system : '/tmp/'"` — AKS included. Observed
  and fixed 2026-08-06 (chart 0.1.2).
  Deliberately **not** `medium: Memory`: a tmpfs `emptyDir` is charged to the container's
  memory limit, which §3 already sizes for `MaxFileBytes × MaxParallelism` buffered in
  memory — backing `/tmp` with it would bill that budget twice. No `sizeLimit` is set;
  bounding node ephemeral storage under load (≤50 MB × 4 in flight per pod) is an open
  follow-up, not a decision this fix made.
  `readOnlyRootFilesystem: true` is retained — the mount is the narrow exception, not a
  relaxation of the constraint.
- **Labels:** standard `app.kubernetes.io/name|instance|version|managed-by` via helpers;
  the Workload Identity pod label renders only when `serviceAccount.azureClientId` is set.

## 5. HPA

`autoscaling/v2`, CPU utilization target 70 %, min 2 / max 6. Memory is intentionally
**not** a scaling metric: the .NET server GC retains heap after bursts (a memory-based
HPA ratchets up and never scales down), per-pod memory is bounded by admission caps
rather than demand, and scale-out cannot relieve pressure on an already-loaded pod. The
2 Gi limit is the memory guardrail. CPU is an imperfect signal for an I/O-bound service —
acceptable; min 2 covers availability, and target/max are values-tunable.

## 6. Versioning: chart ↔ image

- **Image version** — source of truth is a git tag `v<major>.<minor>.<patch>` on `main`;
  the image is pushed to ACR tagged `<major>.<minor>.<patch>`.
- **Chart version** — checked into `Chart.yaml`. Rules:
  - `major.minor` always equals the `major.minor` of the image it deploys.
  - `patch` is the chart's own counter within that series: chart-only changes (template
    fix, new value, probe tuning) bump the chart patch and release a new chart against
    the same image. Several chart versions per image is normal.
  - A new image `minor`/`major` resets the chart to `X.Y.0`; a new image *patch* keeps
    the chart series running (bump chart patch as usual).
  - `appVersion` is stamped by CI at package time (`helm package --app-version`) with the
    exact image version — it is never hand-edited.
- Every image release publishes a chart release (same run); the reverse is not true.

## 7. CI (GitHub Actions)

`ci.yml` (existing, PR + main) gains a `chart-lint` job alongside `build-test`:
`helm lint charts/eugo-docint` and `helm template` with `ci/test-values.yaml` (render
must succeed; offline, no cluster). Lint and render only — no `helm package`, no
`helm push`. `release.yml` is the sole publisher.

**Amended 2026-08-06:** `chart-lint` also asserts that the rendered pod carries a
writable `/tmp` — both the `mountPath: /tmp` and the `emptyDir` backing it, since a
`volumeMount` whose volume is missing renders happily and only fails at apply time.
Rendered from *minimal* values on purpose, so the mount can never become dependent on
some value being set. This is the regression gate for the bug in §4; it is a render
assertion, so it proves the chart's shape, not that extraction succeeds — see §8.

New `release.yml`, triggered by tags:

- **`v*` (image release):** build-test gate → multi-arch buildx push to ACR as
  `eugo-docint:<version>` → chart job: assert `Chart.yaml` `major.minor` matches the tag's
  (fail loudly on drift), `helm package --app-version <version>`, render the packaged
  `.tgz` (`helm lint` + the same two `helm template` value sets as `chart-lint`), then
  `helm push` to the ACR OCI repo (`oci://<acr>.azurecr.io/helm/eugo-docint`).
- **`chart-v*` (chart-only release):** package `Chart.yaml`'s version with
  `--app-version` = highest existing `v<X.Y>.*` git tag of the same `major.minor`, push
  the chart only.
- **Auth:** `azure/login` with OIDC federated credentials (no stored secrets), ACR name
  from a repository variable (e.g. `vars.ACR_NAME`). The ACR resource itself is
  EuGo-infra's to provide; until it exists the release workflow simply isn't run.

## 8. Verification

- API change: the enforced gate — `dotnet restore src/DocInt.slnx` →
  `dotnet build --no-restore` → `dotnet test --no-build` — with the new `/alive` test.
- Chart: `helm lint` + `helm template charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml`
  locally and in CI, and again against the packaged `.tgz` in `release.yml` before it is
  pushed — `helm package` validates `Chart.yaml` metadata but never renders templates, so a
  chart whose templates cannot render packages with exit 0 and would first fail at
  `helm install`. No live-cluster testing in this repo.

**Amended 2026-08-06 — the limit of render-only verification.** The read-only-root/XLSX
bug in §4 rendered, linted and packaged cleanly; it was only observable in a running pod.
Nothing in `dotnet test` can catch it either: the read-only root filesystem comes solely
from this chart's `securityContext`, never from the Dockerfile, so the offline golden
tests and the `docker` CI job both run on a writable filesystem and pass. The gap is
structural — CI's automated gate asserts the *rendered shape* (§7), while the behavioral
proof was done by hand: `kind` cluster, `helm install` from this chart, `POST /v1/extract`
with the BoM XLSX, typed cells returned with no error. Re-run that by hand when changing
the security context or anything about how engines touch the filesystem.

## 9. Documentation updates

- **README:** new *Deploy* subsection — `helm install` example with the values that
  matter (`image.repository`, `serviceAccount.azureClientId`, Azure endpoints), and the
  in-cluster URL.
- **CLAUDE.md:** amend the deployment note — the Helm chart lives at `charts/eugo-docint`
  in this repo (this spec supersedes the "not in this repo" wording); EuGo-infra keeps
  cluster provisioning and release execution. Add the chart lint/template commands and
  the versioning rules to the conventions.
