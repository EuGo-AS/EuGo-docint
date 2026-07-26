# EuGo-docint Helm Chart Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an always-on `/alive` liveness endpoint to the API and a Helm chart at `charts/eugo-docint` (Deployment, ClusterIP Service, Workload-Identity ServiceAccount, HPA), with CI lint/render and a tag-driven release workflow that publishes image + chart to ACR.

**Architecture:** The API change is one endpoint mapping in `Program.cs` (replacing the Development-only Aspire health endpoints). The chart is hand-rolled and minimal — no ingress, PDB, or dependencies; only `apps/v1`, `v1`, `autoscaling/v2`. CI gains a chart lint/render job; a new `release.yml` publishes on `v*` (image + chart) and `chart-v*` (chart only) tags.

**Tech Stack:** .NET 10 minimal API, xUnit + `WebApplicationFactory`, Helm (v4 CLI installed locally), GitHub Actions, ACR (OCI charts).

**Spec:** `docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md` — authoritative for any ambiguity.

## Global Constraints

- Work on a branch cut from `main` (suggested: `helm-chart`); merge to `main` only when green; never commit to `main` directly.
- The enforced .NET gate, always in this order: `dotnet restore src/DocInt.slnx` → `dotnet build --no-restore src/DocInt.slnx` → `dotnet test --no-build src/DocInt.slnx`.
- TDD for the API change: failing test first.
- Target Kubernetes 1.35; `kubeVersion: ">=1.30.0-0"` in `Chart.yaml` (documentation, not a feature gate).
- Chart version `0.1.0`, `appVersion "0.1.0"` — `major.minor` of chart and image stay equal forever; chart `patch` is chart-owned; CI stamps the real `appVersion` at package time.
- No secrets anywhere: Workload Identity only, no API-key values, no `imagePullSecrets`.
- Service port 8090 is baked into the image (`appsettings.json` Kestrel config) — the chart must NOT set any port/URL env var.
- No content-bearing config; only endpoints and sizes.

---

### Task 1: `/alive` liveness endpoint (TDD)

**Files:**
- Modify: `tests/DocInt.Tests/HealthEndpointsTests.cs`
- Modify: `src/DocInt.Api/Program.cs` (lines 61, 67, 79)

**Interfaces:**
- Produces: `GET /alive` → 200 `"Healthy"` in all environments (only `live`-tagged checks); `GET /healthz` unchanged (all checks); `/info.endpoints` includes `"/alive"`. Task 3's probes rely on these exact paths.

- [ ] **Step 1: Write the failing tests**

Add to `tests/DocInt.Tests/HealthEndpointsTests.cs` inside the existing class, after `Healthz_returns_healthy`:

```csharp
    [Fact]
    public async Task Alive_returns_healthy()
    {
        var response = await _client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
```

And extend `Info_returns_service_metadata` with one more assertion after `Assert.Contains("/info", endpoints);`:

```csharp
        Assert.Contains("/alive", endpoints);
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet restore src/DocInt.slnx && dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~HealthEndpointsTests"
```

Expected: `Alive_returns_healthy` FAILS with 404 (NotFound), `Info_returns_service_metadata` FAILS on the `/alive` assertion. (`Healthz_returns_healthy` still passes.)

- [ ] **Step 3: Implement**

In `src/DocInt.Api/Program.cs`:

1. Add to the `using` block at the top:

```csharp
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
```

2. Delete line 61 (`app.MapDefaultEndpoints();`) — it maps `/health` and `/alive` in Development only; keeping it would double-register `/alive` there (AmbiguousMatchException at request time). ServiceDefaults itself stays untouched.

3. Replace the single line `app.MapHealthChecks("/healthz");` with:

```csharp
    app.MapHealthChecks("/healthz");
    app.MapHealthChecks("/alive", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });
```

4. In the `/info` endpoint, change the endpoints array to:

```csharp
        endpoints = new[] { "/v1/extract", "/healthz", "/alive", "/info" }
```

- [ ] **Step 4: Run the full gate to verify green**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Expected: all tests PASS (live-smoke tests self-skip without env).

- [ ] **Step 5: Commit**

```bash
git add tests/DocInt.Tests/HealthEndpointsTests.cs src/DocInt.Api/Program.cs
git commit -m "Add always-on /alive liveness endpoint, drop dev-only Aspire health mappings"
```

---

### Task 2: Chart scaffold — Chart.yaml, values.yaml, helpers, Service, NOTES

**Files:**
- Create: `charts/eugo-docint/Chart.yaml`
- Create: `charts/eugo-docint/values.yaml`
- Create: `charts/eugo-docint/.helmignore`
- Create: `charts/eugo-docint/templates/_helpers.tpl`
- Create: `charts/eugo-docint/templates/service.yaml`
- Create: `charts/eugo-docint/templates/NOTES.txt`
- Create: `charts/eugo-docint/ci/test-values.yaml`

**Interfaces:**
- Produces: helper templates `eugo-docint.fullname`, `eugo-docint.labels`, `eugo-docint.selectorLabels`, `eugo-docint.serviceAccountName` and the values schema — Tasks 3–5 consume these names exactly.

- [ ] **Step 1: Create `charts/eugo-docint/Chart.yaml`**

```yaml
apiVersion: v2
name: eugo-docint
description: Stateless, cluster-internal document-understanding service (files in, Markdown/tables/descriptions out). No ingress by design.
type: application
kubeVersion: ">=1.30.0-0"
# Versioning contract (see docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md §6):
# - version's major.minor always equals the Docker image's major.minor
# - version's patch is chart-owned (several chart versions per image is normal)
# - appVersion is stamped by CI at package time (helm package --app-version); never hand-edit
version: 0.1.0
appVersion: "0.1.0"
```

- [ ] **Step 2: Create `charts/eugo-docint/values.yaml`**

```yaml
image:
  # Required at install time, e.g. <acr>.azurecr.io/eugo-docint
  repository: ""
  # Empty means .Chart.AppVersion
  tag: ""
  pullPolicy: IfNotPresent

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 6
  targetCPUUtilizationPercentage: 70
# Used only when autoscaling.enabled is false
replicaCount: 2

serviceAccount:
  create: true
  # Empty means the chart fullname
  name: ""
  # When set: azure.workload.identity/client-id annotation on the ServiceAccount
  # and azure.workload.identity/use: "true" label on pods
  azureClientId: ""

azure:
  documentIntelligence:
    # Sets env DocumentIntelligence__Endpoint; empty omits the var and the
    # service answers engine_unconfigured per file (designed degraded mode)
    endpoint: ""
  openAI:
    # Sets env AzureOpenAI__Endpoint; empty omits the var
    endpoint: ""
    # Sets env AzureOpenAI__DeploymentNameVision; empty uses the app default (gpt-4.1-mini)
    deploymentNameVision: ""

# Verbatim extra env entries, e.g. DocInt__MaxFileBytes overrides:
# - name: DocInt__MaxFileBytes
#   value: "10485760"
extraEnv: []

resources:
  requests:
    cpu: 250m
    memory: 512Mi
  # No CPU limit: throttling hurts latency more than it helps.
  # Memory is generous on purpose: files are buffered fully in memory
  # (<=50 MB/file, 4 in flight by default).
  limits:
    memory: 2Gi

service:
  port: 8090
```

- [ ] **Step 3: Create `charts/eugo-docint/.helmignore`**

```
.DS_Store
.git/
.gitignore
*.swp
*.bak
*.tmp
*~
ci/
```

- [ ] **Step 4: Create `charts/eugo-docint/templates/_helpers.tpl`**

```yaml
{{- define "eugo-docint.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "eugo-docint.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "eugo-docint.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "eugo-docint.selectorLabels" -}}
app.kubernetes.io/name: {{ include "eugo-docint.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "eugo-docint.labels" -}}
helm.sh/chart: {{ include "eugo-docint.chart" . }}
{{ include "eugo-docint.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "eugo-docint.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "eugo-docint.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}
```

- [ ] **Step 5: Create `charts/eugo-docint/templates/service.yaml`**

```yaml
apiVersion: v1
kind: Service
metadata:
  name: {{ include "eugo-docint.fullname" . }}
  labels:
    {{- include "eugo-docint.labels" . | nindent 4 }}
spec:
  type: ClusterIP
  ports:
    - name: http
      port: {{ .Values.service.port }}
      targetPort: http
      protocol: TCP
  selector:
    {{- include "eugo-docint.selectorLabels" . | nindent 4 }}
```

- [ ] **Step 6: Create `charts/eugo-docint/templates/NOTES.txt`**

```
EuGo-docint {{ .Chart.AppVersion }} deployed (cluster-internal, no ingress).

In-cluster API endpoint:
  http://{{ include "eugo-docint.fullname" . }}.{{ .Release.Namespace }}.svc:{{ .Values.service.port }}/v1/extract

Health:  /healthz (readiness, all checks)   /alive (liveness, self only)
Info:    /info
{{- if not .Values.azure.documentIntelligence.endpoint }}

NOTE: azure.documentIntelligence.endpoint is unset - PDF/DOCX/PPTX/HTML
extraction will answer engine_unconfigured per file.
{{- end }}
{{- if not .Values.azure.openAI.endpoint }}

NOTE: azure.openAI.endpoint is unset - image description will answer
engine_unconfigured per file.
{{- end }}
```

- [ ] **Step 7: Create `charts/eugo-docint/ci/test-values.yaml`**

Exercises every conditional branch (WI label, all env vars, extraEnv):

```yaml
image:
  repository: creugoexample.azurecr.io/eugo-docint
  tag: 0.1.0
serviceAccount:
  azureClientId: 00000000-0000-0000-0000-000000000000
azure:
  documentIntelligence:
    endpoint: https://example-di.cognitiveservices.azure.com/
  openAI:
    endpoint: https://example-aoai.openai.azure.com/
    deploymentNameVision: gpt-4.1-mini
extraEnv:
  - name: DocInt__MaxFileBytes
    value: "10485760"
```

- [ ] **Step 8: Verify lint + render**

```bash
helm lint charts/eugo-docint
helm template smoke charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml
```

Expected: lint passes (0 failures); template renders a Service named `smoke-eugo-docint` with port 8090 and prints NOTES. (Deployment/SA/HPA don't exist yet — that's fine, nothing references them.)

- [ ] **Step 9: Commit**

```bash
git add charts/eugo-docint
git commit -m "Chart scaffold: Chart.yaml, values, helpers, Service, NOTES"
```

---

### Task 3: Deployment template

**Files:**
- Create: `charts/eugo-docint/templates/deployment.yaml`

**Interfaces:**
- Consumes: helpers and values from Task 2; `/alive` + `/healthz` from Task 1.
- Produces: a Deployment whose pod template Task 5's HPA targets via `eugo-docint.fullname`.

- [ ] **Step 1: Create `charts/eugo-docint/templates/deployment.yaml`**

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "eugo-docint.fullname" . }}
  labels:
    {{- include "eugo-docint.labels" . | nindent 4 }}
spec:
  {{- if not .Values.autoscaling.enabled }}
  replicas: {{ .Values.replicaCount }}
  {{- end }}
  selector:
    matchLabels:
      {{- include "eugo-docint.selectorLabels" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "eugo-docint.selectorLabels" . | nindent 8 }}
        {{- if .Values.serviceAccount.azureClientId }}
        azure.workload.identity/use: "true"
        {{- end }}
    spec:
      serviceAccountName: {{ include "eugo-docint.serviceAccountName" . }}
      securityContext:
        runAsNonRoot: true
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: docint
          image: "{{ required "image.repository is required" .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop: ["ALL"]
          ports:
            - name: http
              containerPort: 8090
              protocol: TCP
          livenessProbe:
            httpGet:
              path: /alive
              port: http
            periodSeconds: 10
            failureThreshold: 3
          readinessProbe:
            httpGet:
              path: /healthz
              port: http
            initialDelaySeconds: 3
            periodSeconds: 10
            failureThreshold: 3
          {{- $env := list }}
          {{- with .Values.azure.documentIntelligence.endpoint }}
          {{- $env = append $env (dict "name" "DocumentIntelligence__Endpoint" "value" .) }}
          {{- end }}
          {{- with .Values.azure.openAI.endpoint }}
          {{- $env = append $env (dict "name" "AzureOpenAI__Endpoint" "value" .) }}
          {{- end }}
          {{- with .Values.azure.openAI.deploymentNameVision }}
          {{- $env = append $env (dict "name" "AzureOpenAI__DeploymentNameVision" "value" .) }}
          {{- end }}
          {{- $env = concat $env .Values.extraEnv }}
          {{- with $env }}
          env:
            {{- toYaml . | nindent 12 }}
          {{- end }}
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
```

- [ ] **Step 2: Verify render — full values**

```bash
RENDER="$(mktemp)"
helm template smoke charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > "$RENDER"
grep -c 'azure.workload.identity/use: "true"' "$RENDER"
grep -c 'path: /alive' "$RENDER"
grep -c 'path: /healthz' "$RENDER"
grep -c 'DocumentIntelligence__Endpoint' "$RENDER"
grep -c 'DocInt__MaxFileBytes' "$RENDER"
grep -c 'replicas:' "$RENDER"
```

Expected: `1` for each grep except the last — `replicas:` must print `0` matches (autoscaling on by default ⇒ field omitted; grep exits 1 on zero matches, that's the pass condition).

- [ ] **Step 3: Verify render — bare values (conditionals off)**

```bash
RENDER="$(mktemp)"
helm template smoke charts/eugo-docint --set image.repository=r.example/eugo-docint --set autoscaling.enabled=false > "$RENDER"
grep -c 'replicas: 2' "$RENDER"   # expected: 1
grep -c 'env:' "$RENDER"          # expected: 0 matches (exit 1)
grep -c 'azure.workload.identity' "$RENDER"  # expected: 0 matches (exit 1)
helm template smoke charts/eugo-docint 2>&1 | grep -q 'image.repository is required'  # expected: matches
```

- [ ] **Step 4: Lint and commit**

```bash
helm lint charts/eugo-docint
git add charts/eugo-docint/templates/deployment.yaml
git commit -m "Chart: Deployment with split probes, hardened security context, env from values"
```

---

### Task 4: ServiceAccount template

**Files:**
- Create: `charts/eugo-docint/templates/serviceaccount.yaml`

**Interfaces:**
- Consumes: `eugo-docint.serviceAccountName` helper, `serviceAccount.*` values (Task 2).

- [ ] **Step 1: Create `charts/eugo-docint/templates/serviceaccount.yaml`**

```yaml
{{- if .Values.serviceAccount.create }}
apiVersion: v1
kind: ServiceAccount
metadata:
  name: {{ include "eugo-docint.serviceAccountName" . }}
  labels:
    {{- include "eugo-docint.labels" . | nindent 4 }}
  {{- with .Values.serviceAccount.azureClientId }}
  annotations:
    azure.workload.identity/client-id: {{ . | quote }}
  {{- end }}
{{- end }}
```

- [ ] **Step 2: Verify render**

```bash
helm template smoke charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml | grep -A1 'azure.workload.identity/client-id'
helm template smoke charts/eugo-docint --set image.repository=r.example/eugo-docint --set serviceAccount.create=false | grep -c 'kind: ServiceAccount' || true
```

Expected: first command shows the client-id annotation with the test GUID; second prints `0` (no SA rendered when `create=false`; the Deployment then uses `serviceAccountName: default`).

- [ ] **Step 3: Lint and commit**

```bash
helm lint charts/eugo-docint
git add charts/eugo-docint/templates/serviceaccount.yaml
git commit -m "Chart: ServiceAccount with Workload Identity annotation"
```

---

### Task 5: HPA template

**Files:**
- Create: `charts/eugo-docint/templates/hpa.yaml`

**Interfaces:**
- Consumes: Deployment name via `eugo-docint.fullname` (Task 3), `autoscaling.*` values (Task 2).

- [ ] **Step 1: Create `charts/eugo-docint/templates/hpa.yaml`**

```yaml
{{- if .Values.autoscaling.enabled }}
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ include "eugo-docint.fullname" . }}
  labels:
    {{- include "eugo-docint.labels" . | nindent 4 }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ include "eugo-docint.fullname" . }}
  minReplicas: {{ .Values.autoscaling.minReplicas }}
  maxReplicas: {{ .Values.autoscaling.maxReplicas }}
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
{{- end }}
```

(Memory is deliberately not a metric — spec §5: the .NET server GC retains heap after bursts, so a memory HPA ratchets up and never scales down; the 2 Gi limit is the guardrail.)

- [ ] **Step 2: Verify render**

```bash
helm template smoke charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml | grep -E 'kind: HorizontalPodAutoscaler|minReplicas|maxReplicas|averageUtilization'
helm template smoke charts/eugo-docint --set image.repository=r.example/eugo-docint --set autoscaling.enabled=false | grep -c 'HorizontalPodAutoscaler' || true
```

Expected: first shows HPA with `minReplicas: 2`, `maxReplicas: 6`, `averageUtilization: 70`; second prints `0`.

- [ ] **Step 3: Lint and commit**

```bash
helm lint charts/eugo-docint
git add charts/eugo-docint/templates/hpa.yaml
git commit -m "Chart: autoscaling/v2 HPA, CPU 70%, min 2 / max 6"
```

---

### Task 6: Chart job in ci.yml

**Files:**
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: the chart from Tasks 2–5 and `ci/test-values.yaml`.

- [ ] **Step 1: Append a `chart` job to `.github/workflows/ci.yml`**

Add at the end of the `jobs:` map (sibling of `build-test` and `docker`):

```yaml
  chart:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: azure/setup-helm@v4
      - run: helm lint charts/eugo-docint
      - run: helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > /dev/null
      - run: helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint --set autoscaling.enabled=false > /dev/null
```

- [ ] **Step 2: Verify the workflow file parses and the commands pass locally**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml'))" && echo YAML-OK
helm lint charts/eugo-docint
helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > /dev/null && echo RENDER-OK
```

Expected: `YAML-OK`, lint clean, `RENDER-OK`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "CI: lint and render the Helm chart on every push/PR"
```

---

### Task 7: release.yml — tag-driven image + chart publishing

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: `Chart.yaml` version contract (Task 2), Dockerfile (existing).
- Produces: ACR artifacts `eugo-docint:<version>` (image) and `oci://<acr>.azurecr.io/helm/eugo-docint:<chart-version>`.

Conventions encoded here (spec §6–7): `v<X.Y.Z>` tags release image + chart in one run; `chart-v<X.Y.P>` tags release the chart alone against the newest `v<X.Y>.*` image tag; the job fails loudly if `Chart.yaml`'s `major.minor` drifts from the tag's. Auth is OIDC (`azure/login`), no stored secrets; ACR name and identity come from repository variables `ACR_NAME`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (the ACR itself is EuGo-infra's to provide — until it exists this workflow simply isn't triggered).

- [ ] **Step 1: Create `.github/workflows/release.yml`**

```yaml
name: release

on:
  push:
    tags:
      - "v*"
      - "chart-v*"

permissions:
  contents: read
  id-token: write

jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore src/DocInt.slnx
      - run: dotnet build --no-restore src/DocInt.slnx
      - run: dotnet test --no-build src/DocInt.slnx

  image:
    if: ${{ !startsWith(github.ref_name, 'chart-') }}
    runs-on: ubuntu-latest
    needs: build-test
    steps:
      - uses: actions/checkout@v4
      - uses: azure/login@v2
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
      - run: az acr login --name ${{ vars.ACR_NAME }}
      - name: Strip v prefix
        id: ver
        run: echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/build-push-action@v6
        with:
          context: .
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ vars.ACR_NAME }}.azurecr.io/eugo-docint:${{ steps.ver.outputs.version }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

  chart:
    runs-on: ubuntu-latest
    needs: build-test
    if: ${{ always() && needs.build-test.result == 'success' }}
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
          fetch-tags: true
      - uses: azure/setup-helm@v4
      - uses: azure/login@v2
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
      - run: az acr login --name ${{ vars.ACR_NAME }}
      - name: Resolve versions
        id: versions
        run: |
          set -euo pipefail
          TAG="${GITHUB_REF_NAME}"
          CHART_VERSION="$(sed -n 's/^version: //p' charts/eugo-docint/Chart.yaml)"
          CHART_MM="$(echo "$CHART_VERSION" | cut -d. -f1-2)"
          if [[ "$TAG" == chart-v* ]]; then
            TAG_VERSION="${TAG#chart-v}"
            if [[ "$TAG_VERSION" != "$CHART_VERSION" ]]; then
              echo "::error::tag $TAG says chart $TAG_VERSION but Chart.yaml says $CHART_VERSION"; exit 1
            fi
            APP_VERSION="$(git tag -l "v${CHART_MM}.*" | sort -V | tail -1)"
            if [[ -z "$APP_VERSION" ]]; then
              echo "::error::no image tag v${CHART_MM}.* exists to pair this chart with"; exit 1
            fi
            APP_VERSION="${APP_VERSION#v}"
          else
            APP_VERSION="${TAG#v}"
            APP_MM="$(echo "$APP_VERSION" | cut -d. -f1-2)"
            if [[ "$APP_MM" != "$CHART_MM" ]]; then
              echo "::error::image $APP_VERSION vs chart $CHART_VERSION: major.minor drift"; exit 1
            fi
          fi
          echo "chart_version=$CHART_VERSION" >> "$GITHUB_OUTPUT"
          echo "app_version=$APP_VERSION" >> "$GITHUB_OUTPUT"
      - name: Package and push chart
        run: |
          set -euo pipefail
          helm package charts/eugo-docint \
            --version "${{ steps.versions.outputs.chart_version }}" \
            --app-version "${{ steps.versions.outputs.app_version }}" \
            --destination /tmp
          helm push "/tmp/eugo-docint-${{ steps.versions.outputs.chart_version }}.tgz" \
            "oci://${{ vars.ACR_NAME }}.azurecr.io/helm"
```

Behavior notes: on a `chart-v*` tag the `image` job is skipped and `chart` still runs (`always() && needs.build-test.result == 'success'`). The `chart` job does not `need` the `image` job — a chart referencing a not-yet-pushed image tag only matters at install time, not push time.

- [ ] **Step 2: Verify the workflow parses and the version contract holds**

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml'))" && echo YAML-OK
sed -n 's/^version: //p' charts/eugo-docint/Chart.yaml   # expect 0.1.0
```

Expected: `YAML-OK`; Chart.yaml version prints `0.1.0` (its `major.minor` `0.1` is what a `v0.1.*` tag must match).

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "Release workflow: v* publishes image+chart, chart-v* publishes chart only"
```

---

### Task 8: README + CLAUDE.md updates

**Files:**
- Modify: `README.md` (Run section area)
- Modify: `CLAUDE.md` (architecture note + conventions)

- [ ] **Step 1: Add a Deploy subsection to `README.md`**

Insert after the `## ▶️ Run` section (before `## 🧪 Test`):

````markdown
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
````

- [ ] **Step 2: Amend `CLAUDE.md`**

Replace the line:

```
K8s manifests (`deploy/`) are **not** in this repo — deployment is owned by EuGo-infra.
```

with:

```
The Helm chart lives in `charts/eugo-docint` (per the 2026-07-26 design spec, superseding the earlier "no K8s manifests here" note). EuGo-infra still owns cluster provisioning (AKS, ACR, Workload Identity federation) and release execution. Chart checks: `helm lint charts/eugo-docint` and `helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml`. Versioning: chart `major.minor` always equals the image's; chart `patch` is chart-owned; `appVersion` is CI-stamped at package time — never hand-edit it. Release tags: `vX.Y.Z` = image + chart, `chart-vX.Y.P` = chart only.
```

Also update the architecture tree in CLAUDE.md: add a line for `charts/eugo-docint/` (Helm chart: Deployment, ClusterIP Service, WI ServiceAccount, HPA) under the repo layout listing, next to the `Dockerfile` entry.

- [ ] **Step 3: Verify and commit**

```bash
grep -n "charts/eugo-docint" README.md CLAUDE.md
git add README.md CLAUDE.md
git commit -m "Docs: chart location, deploy instructions, versioning contract"
```

---

### Task 9: Final gate and merge

- [ ] **Step 1: Full verification on the branch**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
helm lint charts/eugo-docint
helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > /dev/null && echo RENDER-OK
```

Expected: all green, `RENDER-OK`.

- [ ] **Step 2: Merge to main and clean up**

```bash
git checkout main
git merge --no-ff helm-chart -m "Helm chart for EuGo-docint: /alive probe endpoint, chart, CI + release flow"
git branch -d helm-chart
```
