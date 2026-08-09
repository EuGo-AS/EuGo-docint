# Azure dependency health on `/healthz` — design

**Date:** 2026-08-07
**Status:** approved in brainstorming; this document is the written spec.
**Supersedes:** one clause of §1 of `2026-07-26-eugo-docint-helm-chart-design.md`. That
section correctly anticipated dependency checks being added *untagged*, so they affect
`/healthz` (readiness) and never `/alive` (liveness) — that half stands. It also stated that
"a slow or unreachable Azure dependency must flip readiness (stop routing traffic)". That
half is reversed here: a failing dependency reports `Degraded` and `/healthz` keeps
answering **200**. Rationale in §1 below.

## Goals

- `/healthz` reports, per configured Azure dependency, whether the pod can currently reach
  it — Document Intelligence and Azure OpenAI — with a reason and a timestamp when it
  cannot.
- The report costs the request path nothing: no network I/O inside a probe request.
- A dependency outage never removes the pod from the Service and never restarts it.

## Non-goals

- No OTel metric or alert for reachability (a later step if wanted; not this one).
- No new route. The report lives on `/healthz`, which is already the readiness probe.
- No chart change, no `values.yaml` entries, no live-smoke test.
- No change to `/alive`, to `/v1/extract`, or to the per-file error contract.

## 1. Behaviour: report, keep serving

A failing dependency yields `HealthStatus.Degraded`, and `/healthz` maps `Degraded → 200`.
The pod stays in the Service and keeps answering requests.

This is deliberate, and it is the point of the feature:

- **The dependency is shared.** All 2–6 replicas talk to the same Foundry resource. A
  dependency blip fails every replica's check simultaneously, so failing readiness does not
  shed load onto a healthy pod — it empties the Service and turns a degraded service into an
  unreachable one. EuGo-Web would get a connection failure instead of the per-file
  `engine_error` inside a `200` that Contract v1 promises.
- **Not every request needs Azure.** XLSX extraction is pure OpenXML and touches no Azure
  endpoint. Evicting the pod would take that path down for a fault it does not share.
- **Degraded already has a designed meaning here.** An unreachable engine surfaces as a
  per-file `engine_error`; an unconfigured one as `engine_unconfigured`. Both are inside a
  `200`. Health reporting should describe that state, not contradict it.

`/alive` is untouched: the new checks carry no `live` tag, so its predicate excludes them,
and it keeps its plain-text body. A dependency outage must never restart a pod that is
serving correctly.

### Why this is not already covered by the startup check

`StartupConnectivityCheck` dials every configured endpoint in `StartingAsync`, before
Kestrel binds, and aborts the host if one is unreachable. So a pod that *starts* has proven
reachability. What is missing is everything after that: a dependency that fails later leaves
a pod that is healthy by every signal the cluster has, while every PDF and photo request
turns into an `engine_error` visible only in a caller's response body. `/healthz` reporting
`Degraded` for a dependency is therefore only ever reachable in that post-boot window —
which is exactly the gap.

## 2. Architecture

```
src/DocInt.Api/Health/
├─ DependencyHealthSnapshot.cs   singleton state: name → (reachable, reason, checkedAtUtc)
├─ DependencyHealthMonitor.cs    BackgroundService: probes on a timer, writes the snapshot
├─ DependencyHealthCheck.cs      IHealthCheck per dependency: reads the snapshot, no I/O
└─ HealthResponseWriter.cs       the /healthz JSON body
src/DocInt.Api/Startup/
└─ AzureFailureDescription.cs    Describe/FirstLine, moved off StartupConnectivityCheck
```

**`IStartupProbe` is reused verbatim.** It already exists once per dependency
(`DocumentIntelligenceStartupProbe` → `GET /documentintelligence/info`,
`AzureOpenAIStartupProbe` → a one-token completion against the vision deployment), is
registered only when that endpoint is configured, and its documented contract is exactly
what is needed here: prove DNS, TLS and credentials; send no document content. Nothing about
the probes changes.

Flow, per tick: `DependencyHealthMonitor` runs every registered `IStartupProbe`
concurrently, each under its own timeout, and writes one `DependencyState` per probe into
`DependencyHealthSnapshot`. On a request, each `DependencyHealthCheck` reads its own key out
of the snapshot and returns. The read is a dictionary lookup — sub-millisecond, no lock
contention, no allocation of consequence.

**Why the snapshot is filled by a timer and not by the request.** The readinessProbe in
`charts/eugo-docint/templates/deployment.yaml` sets no `timeoutSeconds`, so the kubelet's
default of **1 second** applies. A handler that dials Azure (the startup probe budgets 4s
per attempt) would blow that deadline and fail the probe *on timeout*, producing exactly the
eviction cascade §1 exists to prevent — while returning 200. Decoupling the I/O from the
request path is what makes the "keep serving" guarantee hold.

The snapshot is **per pod, in process**. Nothing external, nothing shared: a Workload
Identity token and a DNS resolution are per-pod facts, so pod A must not report pod B's
connectivity. It is lost on restart, which is correct — a fresh pod should not inherit a
verdict it did not make. This does not touch the stateless/storage-free constraint, which is
about document persistence.

**Why not fold this into `StartupConnectivityCheck`.** The two have opposite failure
semantics: startup is fatal and Polly-retried (a throw aborts `app.Run()`), periodic is
informational and never retried (the next tick *is* the retry). Merging them would put a
hot loop inside a class whose documented purpose is "runs once, before Kestrel binds" and
require conditionally bypassing its retry pipeline. The one thing they genuinely share —
formatting an Azure exception to a single truncated line — is extracted instead.

### Registration

The per-dependency `IHealthCheck` registrations go into the existing
`StartupConnectivityCheckExtensions.AddStartupConnectivityCheck`, beside the probe
registrations they mirror. One configured endpoint → one probe → one check, in one place, so
the lists cannot drift. `AddHealthChecks()` is additive, so the `self` check ServiceDefaults
registers earlier (`Program.cs:24`) survives.

With both endpoints blank — the stub-first deployment and the default test factory — no
probes are registered, the monitor returns immediately without starting its timer, and
`/healthz` lists only `self`.

## 3. Configuration

A new section under `DocInt`, mirroring `StartupProbeOptions`:

```jsonc
"DocInt": {
  "DependencyCheck": { "Enabled": true, "IntervalSeconds": 30, "TimeoutSeconds": 4 }
}
```

Defaults live in `appsettings.json` only. Following the existing convention in
`DocIntOptions.cs`, the numbers get **no property initializers** — a deleted section fails
`ValidateOnStart` instead of falling back to a second set of defaults hidden in the class.
`Enabled` carries its `true` default in code for the reason `StartupProbeOptions.Enabled`
does: a bool has no "absent", and defaulting to false would turn a typo into silent
non-reporting.

`ValidateOnStart` asserts `IntervalSeconds > 0`, `TimeoutSeconds > 0`, and
`TimeoutSeconds < IntervalSeconds` — a timeout at or above the interval lets a slow probe
overlap the next tick.

At 30s the cost is 2 calls/min per dependency per pod, fixed regardless of probe frequency
or replica count. For Azure OpenAI that is a one-token completion drawing on the same
TPM/RPM quota as real vision traffic; at 6 replicas, 12 completions/min. That is the price
of the check and it is why the interval is a knob.

**`Enabled: false` registers neither the monitor nor the checks**, so `/healthz` reports
exactly what it does today — only `self`. Registering the checks without the monitor that
feeds them would leave every dependency pinned at `Degraded / not yet checked`, turning an
off switch into a permanent false alarm. Off means silent, not stuck.

**`Enabled` is independent of `DocInt:StartupProbe:Enabled`.** A laptop outside the VNet
turns off both, explicitly. Deriving one default from another section would be exactly the
hidden fallback that `DocIntOptions.cs`'s comments exist to forbid.

**No chart change.** `/healthz` still answers 200, so the readinessProbe is unaffected, and
`extraEnv` already covers overriding the interval on a release.

## 4. Response contract

`/healthz` gains a JSON `ResponseWriter`, replacing the framework's minimal plain text:

```jsonc
{
  "status": "Degraded",
  "checks": [
    { "name": "self", "status": "Healthy" },
    { "name": "Document Intelligence", "status": "Healthy",
      "endpoint": "https://aif-eugo-swc.cognitiveservices.azure.com/",
      "lastCheckedUtc": "2026-08-07T10:12:03Z" },
    { "name": "Azure OpenAI", "status": "Degraded",
      "endpoint": "https://aif-eugo-swc.openai.azure.com/",
      "lastCheckedUtc": "2026-08-07T10:12:03Z",
      "reason": "HTTP 403: Public access is disabled." }
  ]
}
```

- Check names are the probes' existing `Service` strings, so the body and the startup log
  lines name a dependency identically.
- `endpoint`, `lastCheckedUtc` and `reason` are omitted when absent, which is why `self`
  carries only a status.
- `status` is the report's aggregate — the worst entry status.

Two properties are set **explicitly in code** rather than inherited from framework defaults,
because the guarantee in §1 rests on them and a silent change would not fail any test that
merely parses the body:

- `/healthz` sets `ResultStatusCodes` so that `Healthy → 200`, `Degraded → 200`,
  `Unhealthy → 503`. Remapping `Degraded` later has to be a deliberate edit.
- `/healthz` and `/alive` keep **separate** `HealthCheckOptions` instances. `/alive` gets no
  `ResponseWriter` and keeps its plain-text `Healthy` body.

The body names Azure hostnames and a one-line failure reason on an unauthenticated route.
That is acceptable here and only here: the service is cluster-internal with no ingress, and
the startup logs already record the same hostnames verbatim. It stays within the
no-document-content rule by construction — probes send fixed literals, so there is nothing
of a caller's to leak.

**A configured but not-yet-probed dependency reports `Degraded`, reason `not yet checked`.**
It is *not* seeded from the startup check's result: that check may have been disabled, and
inheriting a verdict it never made would be a lie. The window is one probe round-trip after
boot and has no probe consequence, since the readinessProbe reads only the status code.

## 5. Failure handling

- Probe throws → `Degraded` with `reason` from the shared `AzureFailureDescription.Describe`
  (status first, one line, truncated at 200 chars — Azure's messages run to many lines and
  can echo the request back). Timeout → `"timed out"`.
- **The loop body is wrapped in try/catch.** An exception escaping `ExecuteAsync` stops the
  host under .NET's default `BackgroundServiceExceptionBehavior.StopHost`. A monitor that
  kills the pod would invert the entire purpose of the feature.
- Shutdown cancellation is distinguished from a probe timeout: it is neither a failure nor a
  warning.
- **Logging on transition only** — `Warning` when a dependency goes unreachable,
  `Information` when it recovers. Nothing per tick; at 30s that would be 2,880 lines per pod
  per day. Endpoints and reasons only.

## 6. Testing

TDD per repo convention: failing test first, then the code.

| Test | Proves |
| --- | --- |
| `Healthz_reports_degraded_dependency_but_stays_200` | The load-bearing one. Failing fake probe → HTTP **200**, body names the dependency and its reason. Fails if `Degraded` is ever remapped to 503. |
| `Alive_unaffected_by_degraded_dependency` | `/alive` returns 200 and exactly `Healthy` while a dependency is degraded — guards the eviction cascade and the separate-options requirement. |
| `Healthz_returns_healthy` (updated) | The existing literal `"Healthy"` assertion moves to the JSON shape. |
| `DependencyHealthCheck` unit tests | Reachable → Healthy; unreachable → Degraded + reason; never probed → Degraded `not yet checked`. |
| `ProbeOnceAsync` unit tests | Success writes reachable; a throw becomes a one-line reason; a hang becomes `timed out` within `TimeoutSeconds`; zero registered probes is a no-op. |
| Transition-logging test | Two consecutive failures produce one `Warning`, not two. Uses the existing `CapturingLoggerProvider`. |
| Options validation | `TimeoutSeconds >= IntervalSeconds` fails `ValidateOnStart`. |

The endpoint tests follow the `ProbeFactory` pattern already in
`StartupConnectivityCheckTests`: subclass `DocIntAppFactory`, swap `IStartupProbe` for a
fake, and set `StartupProbe:Enabled=false` so the host can boot *with* an unreachable
dependency — the only way to reach the degraded state under test, since a startup check that
is on would abort the boot instead.

The monitor's test seam is `internal Task ProbeOnceAsync(CancellationToken)`; the timer loop
around it stays small enough to read. No `TimeProvider` and no `FakeTimeProvider` package:
timestamps are asserted as recent, not exact.

Everything runs offline against fakes. No Azure credentials, no network.

## 7. Found during implementation, deliberately not fixed

Raised by the per-task reviews, adjudicated as Minor, and deferred rather than dropped.
Recorded here because the review ledger they came from was scratch. None blocks merge; the
first two are the ones with real teeth.

- **The 200-character truncation boundary is unguarded repo-wide.** `A_long_message_is_truncated`
  proves *that* a long message is shortened but not *where*: raising `FirstLine`'s threshold to
  220 would still pass it, and `StartupConnectivityCheckTests` does not cover truncation at all.
  So no test anywhere pins the number the two callers share.
- **`DependencyHealthMonitor.ExecuteAsync`'s try/catch loop has no automated test.** All six
  monitor tests call `ProbeOnceAsync` directly, so the guarantee that a probe throw never takes
  the pod down rests on inspection alone. This follows from the decision in §6 to forbid
  `FakeTimeProvider` — testing the loop needs controllable time.
- **The first successful probe on a cold start logs "is reachable again"** though nothing was
  ever unreachable. Comes verbatim from the plan, not an implementer choice; the fix is a
  three-state initial value rather than a boolean.
- **`A_timeout_that_does_not_fit_inside_the_interval_fails_host_startup` asserts only
  `ThrowsAny<Exception>`.** Matches the file's existing `Malformed_endpoint_fails_host_startup`
  pattern, and the reviewer's mutation analysis found the paired defaults test constrains it
  adequately — so tightening it is a consistency question for the whole file, not this one test.
- **`AzureFailureDescriptionTests`' class doc describes the periodic monitor**, not the string
  formatter the class actually covers. Inherited verbatim from the task brief.
