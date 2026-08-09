# Admission control and the autoscaling signal — design

**Date:** 2026-08-08
**Status:** approved in brainstorming; this document is the written spec.
**Extends:** `2026-07-26-eugo-docint-helm-chart-design.md` §3 (resources) and §5 (HPA), and the
`DocInt:*` limit table in `2026-07-19-eugo-docint-design.md`. It adds one limit, one option
group, one component, one instrument and one HPA metric.
**Depends on:** the `feat/usage-counters` branch, unmerged at the time of writing. `DocIntTelemetry`,
`RejectReasons` and `docint.rejected_requests` all live there, and this design extends all three.
Basing this work on `main` would mean extending code that does not exist there.

## Goals

- Make the pod's memory ceiling a property of its configuration rather than of whatever the
  caller happened to send.
- Give the HPA a signal that moves with real load, so scale-out actually fires on the
  Azure-bound path.
- Make overload produce a retryable answer instead of an OOMKill that drops every in-flight
  request in the pod.

## Non-goals

- **No custom or external metrics.** `../EuGo-infra` provisions no AKS cluster, no KEDA, no
  Prometheus and no OTel collector. The HPA is therefore restricted to `metrics.k8s.io`
  Resource metrics — `cpu` and `memory`. An `files_in_flight` gauge scaled by KEDA is the
  better long-term signal and remains out of scope until something exists to read it.
- **No change to the per-file contract.** Per-file success and failure stay inside the 200.
  `kind`, `markdown`, `tables`, `imageDescription`, `warnings`, `error` are untouched.
- **No request-level queue depth, no priority, no fairness.** First-come, first-served.
- **No GC tuning.** Considered and explicitly declined — see §8.
- No new route; no change to `/healthz`, `/alive` or `/info`.

## 0. Two problems, one of which autoscaling cannot solve

The HPA targets CPU at 70% (`charts/eugo-docint/templates/hpa.yaml`). On the dominant path —
PDF, DOCX, PPTX, HTML and images, all of which are Azure round-trips — the pod is blocked on
I/O. CPU stays low while memory climbs with buffered file bytes, so the resource that actually
saturates never triggers a scale-out.

That is the stated problem. Underneath it is a second one that a better signal does not fix:
there is no upper bound on concurrent requests. Kestrel sets no `MaxConcurrentConnections`,
there is no rate limiter and no queue, and `MultipartExtractRequestReader` holds every accepted
file's `byte[]` for the whole request. Peak memory is therefore
`bytes-per-request × concurrent-requests`, unbounded, against a 2 GiB limit. The HPA reacts in
tens of seconds; a burst can allocate a gigabyte in a few. **Autoscaling can never be the
defence against OOM.** Backpressure is.

So this spec does both, and the order matters: the gate is what makes memory bounded, and only
a bounded memory reading is worth autoscaling on.

## 1. The invariant

After this change, a pod holds:

```
peak memory ≈ baseline + AdmissionBudgetBytes
```

`baseline` is the idle .NET working set (~150–250 MiB observed range for this shape of service;
not measured on AKS yet). `AdmissionBudgetBytes` is configuration. Nothing the caller sends can
move that ceiling — only a config change can.

This replaces the sizing claim in `values.yaml` (`≤50 MB/file, 4 in flight by default`) and in
chart-design §3 / §4, both of which are wrong today: the `4` is `MaxParallelism`, which is
per *request*, while requests themselves are unbounded. See §9.

**What the budget does not bound.** It bounds *live* allocation, not RSS. A lease is released
when the request completes, at which point the `byte[]`s are unreachable but not yet collected.
RSS therefore lags the budget by however much garbage is pending, which is the second reason
the HPA threshold in §7 is a starting point rather than a derived number.

## 2. `RequestAdmissionGate`

A singleton in `src/DocInt.Api/Admission/`, wrapping a permit-based limiter.

```csharp
Task<AdmissionLease?> AcquireAsync(long bytes, CancellationToken ct);  // null => queue timed out
public sealed class AdmissionLease : IDisposable;                      // releases permits
```

**Permits are denominated in MiB**, `ceil(bytes / 1 MiB)`, minimum 1. A 1 GiB budget is 1024
permits; a 200 MiB request takes 200. Integer permits at byte granularity would need a 64-bit
permit count and buy nothing — the budget is a safety margin, not an accounting ledger.

**Acquisition is all-or-nothing and happens once, before any buffering.** This is the load-bearing
property. See §5.

**`Enabled: false` is a total pass-through** — no lease, no wait, no counter. Same escape-hatch
shape as `StartupProbe`, `DependencyCheck` and `DuplicateTracking` already use. It disables
*admission* only: `MaxRequestFileBytes` and the Kestrel ceiling in §4 are ordinary request
limits and stay enforced regardless, so a disabled gate cannot reopen the 1.56 GiB body.

## 3. Configuration

New group `DocInt:Admission:*`, and one new top-level limit.

| Key | Default | Meaning |
| --- | --- | --- |
| `DocInt:MaxRequestFileBytes` | `209715200` (200 MiB) | Cap on the **sum** of accepted file bytes in one request |
| `DocInt:Admission:Enabled` | `true` | `false` disables the gate entirely |
| `DocInt:Admission:BudgetBytes` | `1073741824` (1 GiB) | In-flight ceiling per pod |
| `DocInt:Admission:QueueTimeoutSeconds` | `10` | How long a request waits for budget before being shed |
| `DocInt:Admission:RetryAfterSeconds` | `5` | Value of the `Retry-After` header on a 503 |

`MaxFileBytes` (50 MiB) and `MaxFilesPerRequest` (32) are **unchanged**. Both remain meaningful
for realistic submissions; `MaxRequestFileBytes` bounds the pathological combination of the two.

### Startup validation (`ValidateOnStart`, alongside the existing rules)

- `MaxRequestFileBytes >= MaxFileBytes` — a single maximum-size file must be admissible.
- `BudgetBytes >= MaxRequestBytes` — **this is what makes an over-budget request impossible.**
  Kestrel already refuses anything above `MaxRequestBytes`, so every request that reaches the
  gate necessarily fits within the budget. The gate can therefore only ever admit or time out;
  there is no "never satisfiable" runtime branch, because a release that would create one fails
  to boot instead. This is the same fail-fast-at-startup discipline the other limits use.
- `QueueTimeoutSeconds > 0`, `RetryAfterSeconds > 0`, `BudgetBytes > 0`.

## 4. `MaxRequestFileBytes` and the Kestrel ceiling

`DocIntOptions.MaxRequestBytes` changes:

```
before:  MaxFileBytes * MaxFilesPerRequest + 1 MiB   = 1 678 770 176  (~1.56 GiB)
after:   MaxRequestFileBytes + 1 MiB                 =   210 763 776  (~201 MiB)
```

The 1 MiB of slack covers multipart framing and the `hints` part, exactly as before. This is
what stops Kestrel accepting a body the pod cannot hold — today `MaxRequestBodySize` is set to
a value 78% of the way to the container's entire memory limit.

Against the 1 GiB budget, that puts per-pod concurrency at **~5 simultaneous maximum-size
requests**, or proportionally more for realistic ones: a 10 MB submission takes 10 of 1024
permits, so ordinary traffic is nowhere near the gate. The gate is a ceiling on the
pathological case, not a throttle on normal use.

Enforcement is in two places, deliberately:

1. **Declared size** — the existing `request.ContentLength > MaxRequestBytes` check in
   `MultipartExtractRequestReader.ReadAsync`, unchanged, still yielding `body_too_large`.
2. **Observed size** — a new running sum of accepted file bytes while buffering, yielding the
   new reason `request_files_too_large`. Necessary because `Content-Length` is absent under
   chunked transfer encoding and can simply be wrong.

Both are 400s: a request that exceeds a documented request-level cap is malformed, the same
class as `too_many_files`. This is distinct from the per-file `too_large`, which stays a per-file
error inside a 200.

## 5. Where the gate sits, and why not elsewhere

**An endpoint filter on `/v1/extract`**, acquiring before `MultipartExtractRequestReader` runs
and releasing when the handler returns:

```
Kestrel (MaxRequestBodySize) → endpoint filter (acquire) → reader → ExtractionService → 200
                                    │
                                    └─ timeout → 503 + Retry-After
```

The reservation is `Content-Length` when present, and `MaxRequestBytes` when absent — a
conservative full reserve for chunked bodies. `Content-Length` covers the whole multipart body
rather than just the file parts, which is the right basis: framing and the hints part occupy
memory too.

**Rejected: accounting per file inside the reader.** Exact, and deadlock-prone — several
requests each hold partial budget while waiting for more, and none can proceed until a queue
timeout fires. Classic hold-and-wait. It also allocates before deciding, which defeats the
purpose: by the time file 3 of 5 cannot get budget, three files are already resident.

**Rejected: reserve on `Content-Length`, then reconcile to actual.** Adds a two-phase accounting
path and a partial-release step on every request to buy precision the design does not need.

**Not middleware.** A filter is scoped to the one route that buffers; middleware would need a
path test to avoid gating `/healthz` and `/alive`. The cost is that the lease releases when the
filter returns, marginally before the response body is written — the response holds Markdown
strings, not file bytes, so the difference does not affect the ceiling.

## 6. Telemetry and the wire contract

**New instrument** on the existing `EuGo.DocInt` meter:

| Instrument | Type | Unit | Tags | Max series |
| --- | --- | --- | --- | --- |
| `docint.shed_requests` | `Counter<long>` | `requests` | `reason` | 1 |

`reason` is closed and currently has exactly one value, `queue_timeout`. It exists so the
vocabulary can grow without a breaking dashboard change.

**Deliberately not folded into `docint.rejected_requests`.** That instrument is specified as
"requests rejected as malformed (400)" with a closed reason vocabulary. A 503 is not a malformed
request — it is a well-formed request the pod declined to start. Merging them would make the
counter mean "requests that did not run", which no dashboard question asks.

**New reject reason.** `RejectReasons.RequestFilesTooLarge = "request_files_too_large"`, taking
the vocabulary from 8 values to 9.

**New status code.** `503 Service Unavailable` with `Retry-After: 5`, body via `Results.Problem`
so it matches the existing 400's shape, and `.ProducesProblem(StatusCodes.Status503ServiceUnavailable)`
on the endpoint so it appears in the OpenAPI document. This is an addition to contract v1, which
`CLAUDE.md` marks internal-may-change until EuGo-mcp becomes the second consumer. EuGo-Web should
treat it as retryable; a sustained rate of it means replicas are needed.

**Client disconnect while queued** cancels the wait through the request token. Nothing is
written, no permit is held, and no counter moves — an abandoned request has no outcome to report,
consistent with how `ExtractionService` already treats request abandonment.

## 7. The HPA

```yaml
metrics:
  - type: Resource                      # kept — the XLSX path is CPU-bound
    resource: {name: cpu,    target: {type: Utilization,  averageUtilization: 70}}
  - type: Resource                      # new
    resource: {name: memory, target: {type: AverageValue, averageValue: 900Mi}}
behavior:
  scaleDown:
    stabilizationWindowSeconds: 600
```

**Why `AverageValue` and not `Utilization`.** Utilization is a percentage of
`resources.requests.memory`, which is 512Mi — a scheduling hint, not the real ceiling. A pod
holding 900Mi would read as 176% and peg the HPA at `maxReplicas` immediately. `AverageValue`
compares against an absolute number that can be reasoned about against the 2Gi limit.

**Where 900Mi comes from.** Baseline (~200Mi) plus roughly two-thirds of the 1 GiB budget. A fully
occupied pod sits near 1224Mi live, so the trigger fires *before* saturation rather than at it,
leaving headroom under the 2Gi limit for uncollected garbage.

**Correction: 1224Mi under-counts simultaneous transient allocation, by roughly 30%.**
`MultipartExtractRequestReader.BufferAsync` grows an unsized `MemoryStream` by doubling — 64 MiB of
capacity to hold a 50 MiB part — and then calls `ToArray()` while that buffer is still live, so the
file being buffered peaks near 64 MiB (the `MemoryStream`) + 50 MiB (the `ToArray()` copy) rather
than 50 MiB, on top of whatever earlier files in the same request already retained. A 201 MiB
request split 4×50 MiB therefore peaks near **264 MiB**, not 201 MiB — about 1.29 GiB at 5
concurrent requests rather than the 1.0 GiB the arithmetic above implies. Total RSS still lands
near 1.5 GiB against the 2Gi limit, so headroom is closer to ~25% than the ~40% the current
arithmetic implies. This is *simultaneous liveness* — bytes live and counted at the same instant,
before any lease is released — distinct from the garbage-lag caveat in §1, which is about RSS
trailing a lease that has already been released. The obvious mitigation, not part of this change:
pre-size `BufferAsync`'s `MemoryStream` to the part's cap (`MaxFileBytes`) instead of letting it
grow by doubling, which removes the extra live copy.

**Both metrics are kept** because the two paths saturate differently: XLSX runs through the
synchronous `SpreadsheetEngine` and is CPU/thread-pool bound, everything else is Azure-bound.
`autoscaling/v2` computes a recommendation per metric and takes the maximum.

**`autoscaling.targetMemoryAverageValue: ""` omits the memory metric entirely**, following the
chart's existing omit-when-empty idiom. This is the documented back-out if §8 turns out to cost
more than it saves.

## 8. What this makes worse, on purpose

**Scale-down gets worse before it gets better.** Today, CPU-only scale-down works promptly —
precisely because CPU is never high. Adding memory changes that: `autoscaling/v2` takes the
maximum of the per-metric recommendations, so a sticky memory reading pins the replica count
regardless of what CPU says.

The stickiness is real and known. The pod runs **Server GC** — confirmed as
`"System.GC.Server": true` in the built `DocInt.Api.runtimeconfig.json`, emitted by the Web SDK
even though nothing in the repo requests it — and the chart sets no CPU limit, so the GC sizes
its heaps to the node's core count and returns memory to the OS lazily. After a burst, the
working set can stay elevated for an unpredictable stretch.

**The threshold has to satisfy two constraints that may not both be satisfiable**: below
saturation, so it fires at all; above post-burst retained memory, so it ever releases. 900Mi is
a considered guess, not a derived value, and it cannot be validated without real pod metrics —
which requires a collector EuGo-infra has not built. `stabilizationWindowSeconds: 600` is the
guard in the meantime.

**The named escalation, if pinning shows up as cost:** constrain the GC. Reviewed during
brainstorming and declined in favour of the smaller diff. Recorded here so the option is not
rediscovered from scratch:

- `<ServerGarbageCollection>false</ServerGarbageCollection>` in `DocInt.Api.csproj` — a single
  heap, working set tracking in-flight bytes closely, at some throughput cost on the XLSX path.
- Or keep Server GC and pin `System.GC.HeapCount` plus `System.GC.ConserveMemory` (0–9) via
  `RuntimeHostConfigurationOption` items.

Precedence is `DOTNET_gcServer` (env) > `System.GC.Server` (runtimeconfig.json) > runtime default,
so a chart env var overrides a csproj setting without a rebuild. **Trap:** integer GC values read
from environment variables are parsed as *hexadecimal*, so `DOTNET_GCHeapCount=10` means 16 heaps.
Numeric GC knobs belong in the csproj, where they are decimal; booleans like `gcServer=0` are
unaffected.

## 9. Corrections to existing documentation

Both are edited in this slice, because this change is what makes the correct number knowable.

- `charts/eugo-docint/values.yaml` — the `resources` comment ("files are buffered fully in
  memory (≤50 MB/file, 4 in flight by default)") states a per-pod bound of 200 MB. The `4` is
  `MaxParallelism`, which is per request; requests are unbounded, so the real bound was
  `bytes-per-request × concurrent-requests`. Replaced with the §1 invariant.
- `2026-07-26-eugo-docint-helm-chart-design.md` §3 and §4 — the same error, including the
  `/tmp` `emptyDir` note, which sizes OpenXML spill at "≤50 MB × 4 in flight per pod". Spill
  scales with concurrent XLSX files, which the gate now bounds. The open follow-up on
  `sizeLimit` stays open.

## 10. Testing

| Layer | Cases |
| --- | --- |
| Unit — `RequestAdmissionGate` | acquire/release round-trip; MiB rounding (1 byte → 1 permit); a second request blocks while the budget is held; queue timeout returns `null`; `Enabled=false` passes through without a lease; disposal releases on the exception path |
| Unit — options | new defaults asserted beside the existing ones in `OptionsTests`; `ValidateOnStart` rejects `BudgetBytes < MaxRequestBytes`, `MaxRequestFileBytes < MaxFileBytes`, and non-positive values |
| Contract | file-byte sum over `MaxRequestFileBytes` → 400 with `request_files_too_large` counted; a saturated pod → 503 carrying `Retry-After`, driven by a small `DocInt:Admission:BudgetBytes` via `UseSetting` (the pattern already used for `DocInt:MaxFilesPerRequest`) |
| Chart | `helm template` assertions: the memory metric renders with the configured value; the `scaleDown` window renders; `targetMemoryAverageValue: ""` omits the memory metric while leaving CPU; the new `docint.*` values reach the pod as env, and render nothing when unset |

**The gate stays enabled in `DocIntAppFactory`.** A 1 GiB budget never blocks kilobyte fixtures,
so existing tests are unaffected while the wiring gets genuine coverage; saturation tests shrink
the budget explicitly. This differs from the probe-consumer pattern, which blanks `Enabled` in
the factory — the gate is not an `IStartupProbe` consumer and cannot perturb attempt counts.

**Verification note for the plan.** `chart-lint` is red on this branch independently of this
work: `.github/workflows/ci.yml` carries an uncommitted step asserting the OTLP env vars, whose
chart half is the unfinished last task of the usage-counters plan. A green CI job is therefore
not available as the signal for chart changes here; local `helm template` runs are (helm 4.1.4
is installed).

## 11. Deferred

- **`files_in_flight` / budget-occupancy as an External metric via KEDA.** The correct signal.
  Blocked on EuGo-infra providing a cluster, a collector and KEDA. When it exists, it should
  replace the memory metric rather than join it — occupancy is what memory is being used to
  approximate here.
- **Tuning `targetMemoryAverageValue` against measured pod metrics.** Blocked on the same.
- **A wait-duration histogram** (`docint.admission_wait`). Useful for choosing
  `QueueTimeoutSeconds` empirically; omitted from v1 because nothing scrapes it yet.
- **Bounding node ephemeral storage for `/tmp`** (`sizeLimit` on the `emptyDir`). Pre-existing
  follow-up from the 2026-08-06 fix, now more tractable since concurrent XLSX files are bounded.
