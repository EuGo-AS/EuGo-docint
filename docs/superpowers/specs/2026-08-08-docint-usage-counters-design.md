# Usage counters for `/v1/extract` — design

**Date:** 2026-08-08
**Status:** approved in brainstorming; this document is the written spec.
**Extends:** the *Metric* line of `2026-07-19-eugo-docint-design.md` §Engines, which specified
`docint.pages_processed` as the service's single instrument. That counter is unchanged. This
document adds five more on the same meter and one first-class export path in the chart.

## Goals

- Answer, from metrics rather than by grepping logs: how many files has this service processed,
  broken down by type; how many bytes; how long each file took; how often the caller submits
  the same file twice; and how often a request is rejected outright, and why.
- Cost the request path effectively nothing, and never change a response.
- Give an operator a supported way to point the pod at an OTLP collector, so the numbers can
  leave the pod at all.

## Non-goals

- **No metrics backend.** `../EuGo-infra` provisions no collector, no Prometheus, no
  Application Insights. This spec adds the chart value that would target one; standing one up
  is EuGo-infra's work and is not in scope here.
- **No Prometheus exporter and no `/metrics` route.** The OTLP path already exists in
  ServiceDefaults; a second export mechanism is not justified by anything today.
- **No reachability metric.** `2026-08-07-dependency-health-checks-design.md` listed an OTel
  metric for dependency reachability as a deliberate later step. It stays deferred; `/healthz`
  remains the way to see that.
- No behaviour change to `/v1/extract`: no deduplication, no skipped engine calls, no change to
  the per-file error contract or to status codes.
- No new route, no change to `/healthz`, `/alive` or `/info`.

## 1. The instrument set

All six live on the existing meter `EuGo.DocInt` (`DocIntTelemetry.MeterName`) and follow the
existing naming style — `docint.<noun>`, underscores, not OTel dotted style — so
`pages_processed` does not become the odd one out.

| Instrument | Type | Unit | Tags | Max series |
| --- | --- | --- | --- | --- |
| `docint.pages_processed` *(existing, unchanged)* | `Counter<long>` | `pages` | `kind` | 7 |
| `docint.files_processed` | `Counter<long>` | `files` | `kind`, `outcome` | 56 |
| `docint.bytes_processed` | `Counter<long>` | `By` | `kind` | 7 |
| `docint.file_duration` | `Histogram<double>` | `s` | `kind`, `outcome` | 56 |
| `docint.duplicate_files` | `Counter<long>` | `files` | `scope` | 2 |
| `docint.rejected_requests` | `Counter<long>` | `requests` | `reason` | 8 |

### Tag vocabularies

Three of the four already exist in the codebase and are reused verbatim rather than redefined.

- **`kind`** — the existing `kindName` from `ExtractionService`: `pdf`, `docx`, `pptx`, `html`,
  `xlsx`, `image`, plus `unknown` for a file whose kind was never detected. 7 values.
- **`outcome`** — the existing `outcomeCode`: `ok`, plus the seven `ErrorCodes` values
  (`unsupported_type`, `too_large`, `empty_file`, `corrupt`, `timeout`, `engine_error`,
  `engine_unconfigured`). 8 values.
- **`scope`** — `request` or `pod`. See §3.
- **`reason`** — new, and closed. Every `BadExtractRequestException` throw site gets exactly one:

  | Reason | Thrown by |
  | --- | --- |
  | `body_too_large` | declared `Content-Length` above `MaxRequestBytes` |
  | `not_multipart` | content type is not `multipart/form-data` |
  | `boundary_missing` | multipart boundary absent or blank |
  | `too_many_files` | more `files` parts than `MaxFilesPerRequest` |
  | `hints_too_large` | `hints` part above `MaxHintsBytes` (256 KiB) |
  | `hints_invalid` | `HintsParser` — unparseable JSON, or JSON `null` |
  | `malformed_body` | truncated or corrupt multipart framing |
  | `no_files` | no part named `files` |

  `HintsParser` has two throw sites; both mean "the hints part is unusable" and share
  `hints_invalid`. A distinction between "malformed" and "parsed to null" would be a
  distinction without an operator-visible difference.

### Histogram bucket boundaries

`docint.file_duration` is created with explicit boundaries:

```
0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 25, 50, 100, 250
```

supplied through `InstrumentAdvice<double>.HistogramBucketBoundaries`. The .NET default
boundaries run to 10 000 and are shaped for milliseconds; against a `PerFileTimeoutSeconds`
of 100 they would drop nearly every measurement into the first bucket and the histogram would
carry no information. The top boundary sits above the timeout on purpose, so a measurement
that somehow exceeds it is visible rather than silently clamped.

Seconds, not milliseconds, matching OTel convention and `http.server.request.duration`. The
existing per-file log line stays in milliseconds — a different consumer, deliberately not
changed.

### Why there is no request-count instrument

`AddAspNetCoreInstrumentation` (already registered in ServiceDefaults) emits
`http.server.request.duration` with route and status code, which covers request count, request
latency and the 200/400 split for `POST /v1/extract`. A `docint.requests` counter would
duplicate it and disagree with it at the edges.

## 2. Where the instruments are emitted

Three sites, all of them code paths that already exist.

### 2.1 Inside `ExtractionService`'s `Parallel.ForEachAsync` body

The loop already computes `kindName`, the byte length, `outcomeCode` and elapsed time, and
currently discards the last three into a log line. `files_processed`, `bytes_processed` and
`file_duration` are emitted there, and the file's content hash is computed there (§3).

**Emission is unconditional.** The existing `if (outcome.PagesProcessed > 0)` guard around
`pages_processed` is correct for that instrument and must *not* be copied:

- `files_processed` must count failures, or "files processed" is not a total.
- `bytes_processed` must count bytes **read**, not bytes successfully extracted. A corrupt
  40 MiB file that reports zero destroys the instrument as a capacity signal.

Files rejected by the reader (`file.Error is not null` — `too_large`, `empty_file`,
`unsupported_type`) do flow through this loop, so they are captured, with `kind` = `unknown`.

### 2.2 In `ExtractionService`, after the loop

`duplicate_files`, both scopes, from a single `DuplicateFileTracker.Record` call.

If the loop throws — `EngineRouter` deliberately rethrows `OperationCanceledException` on
genuine request abandonment — `Record` is not reached and the abandoned request contributes no
duplicate measurements. That is intended: an abandoned request has no meaningful outcome.

### 2.3 In `ExtractEndpoint`'s existing `catch (BadExtractRequestException)`

`rejected_requests`, tagged with the exception's new `Reason`. `ExtractEndpoint.Handle` takes
`DocIntTelemetry` as an additional DI parameter.

**This counter is not a complete count of rejected requests.** Kestrel's `MaxRequestBodySize`
is set to `MaxRequestBytes`, so a body that exceeds the cap without a declared `Content-Length`
is terminated by Kestrel before the reader sees it, and never reaches this catch. The counter
counts rejections *docint decided on*; `http.server.request.duration` by status code is the
complete picture.

### 2.4 Supporting change: `BadExtractRequestException.Reason`

```csharp
public sealed class BadExtractRequestException(string reason, string detail) : Exception(detail)
{
    public string Reason { get; } = reason;
}
```

with the eight values as consts on a `RejectReasons` static class in `Validation/`, mirroring
the existing `ErrorCodes` class in `Contracts/`. All nine throw sites pass one. `Reason` is
never written to the response — the 400 body keeps its current `title` and `detail`.

### 2.5 Supporting change: `FileItem.SizeBytes`

`bytes_processed` would otherwise under-report exactly the files that cost the most bandwidth.
`MultipartExtractRequestReader.BufferAsync` returns `([], true)` for an over-cap part: it
drains the stream and discards the count, so `file.Bytes.Length` is `0` for a `too_large`
file. The existing log line already reports `sizeBytes=0` for a rejected 60 MiB upload, which
is misleading today.

`BufferAsync` returns `(byte[] Bytes, long Observed, bool TooLarge)`, where `Observed` is the
total number of bytes read from the part including the drained remainder. `FileItem` gains:

```csharp
public required long SizeBytes { get; init; }
```

`required` on purpose: it makes the compiler find every construction site, including the ones
in the test suite, rather than letting a site default to `0` and silently assert nothing.
`ExtractionService` reads `file.SizeBytes` for both the counter and the log line; the hints
part ignores the new value.

## 3. Duplicate tracking

### 3.1 What is counted

`docint.duplicate_files` carries two disjoint scopes so that an exact number sits next to a
sampled one:

- **`scope=request`** — the same content submitted more than once inside a single multipart
  batch. A hash group of size *n* contributes *n − 1*. Exact, stateless, unaffected by replica
  count.
- **`scope=pod`** — for each *distinct* hash in the batch, one measurement if this pod has
  seen that hash before. Counted once per batch per hash, so it can never overlap the
  within-request count.

Only files with `Error is null` participate. This is load-bearing: a `too_large` file carries
`Bytes = []`, so without the exclusion every over-cap file in a batch would hash identically
and be reported as a duplicate of the others. `empty_file` is excluded by the same rule.

### 3.2 Honesty caveat on `scope=pod`

`scope=pod` is a **lower bound, not a rate.** The Service load-balances across
`minReplicas: 2` or more pods, so a repeated file lands on the pod that saw it roughly 1/N of
the time — the reported number is about 1/N of true cross-request duplication, and it moves
when the HPA scales. It answers "is this happening at all"; it must not be quoted as a
percentage.

This sentence goes in the instrument's own `description` text, not only in this spec, so
whoever finds it in a metrics browser sees the caveat there.

The cache is also per-pod and resets on restart. That is normal for counters, which reset on
restart anyway.

### 3.3 Storage-free compliance

The cache holds 64-bit hashes and nothing else. No document bytes, no filenames, no
reconstructable content — docint does not become a document store. Hashes are never logged,
never used as a metric tag, and never attached to a trace.

### 3.4 Where the hash is computed, and why not in a batch pass

Hashing the batch up front, before the loop, is the obvious design and the wrong one.
`scope=pod` requires *every* accepted file to be hashed — a file that is unique within its
batch still has to be checked against the cache — so the usual "group by length first, hash
only within collision groups" optimisation buys nothing. The worst case is
`MaxFilesPerRequest × MaxFileBytes` = 32 × 50 MiB = 1.6 GiB through the hash, roughly 300 ms
single-threaded, sitting on the request's critical path.

The hash is therefore computed **inside** the parallel loop body, before routing, where it runs
at `MaxParallelism`. What that buys differs by engine, and the difference is worth stating
precisely:

- For the four DI kinds (`pdf`, `docx`, `pptx`, `html`) and for `image`, the hash overlaps an
  Azure round-trip that dominates wall-clock by orders of magnitude. Genuinely free.
- For `xlsx` it is **not** free. `SpreadsheetEngine` is fully synchronous — `Task.FromResult`,
  no network — so on the Azure-free path the hash is added CPU on the critical path with
  nothing to hide behind. The cost is bounded and small: 1.6 GiB worst case is roughly 40 ms
  spread across four threads, against an operation already measured in hundreds of
  milliseconds for a large workbook.

The design stands on that second bullet, not the first: the additive cost on the one engine
that has nowhere to hide it is still an acceptable fraction of the work being done.

The hashes are collected in a `ulong?[]` local indexed by `file.Index` — `null` for a file
excluded under §3.1 — rather than on `FileItem`. `FileItem` models the file as it moves through
validation and routing; a hash exists only to feed a counter and does not belong on it. After
the loop, `ExtractionService` drops the nulls and passes the remaining values to `Record`; a
batch in which every file was excluded yields an empty list and no measurements.

### 3.5 `DuplicateFileTracker`

A singleton in `src/DocInt.Api/Telemetry/`, one public method:

```csharp
public sealed record DuplicateCounts(int WithinRequest, int AcrossRequests);

public sealed class DuplicateFileTracker(IOptions<DuplicateTrackingOptions> options)
{
    public DuplicateCounts Record(IReadOnlyList<ulong> hashes);
}
```

- **Algorithm:** `withinRequest = hashes.Count − distinct.Count`; then for each distinct hash,
  count a hit if the cache contains it; then insert every distinct hash.
- **Cache:** `HashSet<ulong>` for lookup plus `Queue<ulong>` for FIFO eviction order, both
  behind one lock. At `Capacity` = 100 000 that is roughly 3 MB — flat, and small against the
  pod's 2 GiB limit and the up-to-200 MB of file bytes already held in flight. One lock
  acquisition per request, so contention is nil.
- **It does not touch the meter.** `Record` returns counts and `ExtractionService` emits them.
  This keeps the tracker unit-testable with no DI, no host and no `IMeterFactory`.
- **Zeros are emitted.** When tracking is enabled, `ExtractionService` calls `Add` for both
  scopes on every batch, including when the count is `0`. This is what makes §4's promise true:
  an enabled tracker with nothing to report produces a flat zero line, and only a *disabled*
  tracker produces no series at all. A dashboard can then distinguish "no duplicates" from
  "not measured", which is the entire point of the distinction.

### 3.6 Hash function

`XxHash64.HashToUInt64` from `System.IO.Hashing` **10.0.10** — the latest stable on the 10.x
line, matching the `Microsoft.AspNetCore.OpenApi 10.0.10` already pinned in
`DocInt.Api.csproj`. Purpose-built for this, Microsoft-owned, and its 64-bit output is exactly
what the cache stores, with no truncation step to justify.

Collision risk is immaterial at this scale: with a 100 000-entry cache the birthday-bound
probability of a false duplicate is on the order of 10⁻⁹, and the consequence of one is a
single over-count on a diagnostic counter.

The zero-dependency alternative is in-box `SHA256.HashData` truncated to 8 bytes — roughly 5×
slower and still acceptable given the overlap in §3.4. Recorded here as a one-line swap if the
package reference is ever unwanted.

## 4. Configuration

New options class bound at `DocInt:DuplicateTracking`:

| Key | Default | Source of the default |
| --- | --- | --- |
| `DocInt:DuplicateTracking:Enabled` | `true` | **code**, as a property initializer |
| `DocInt:DuplicateTracking:Capacity` | `100000` | **`appsettings.json`**, no initializer |

The split is deliberate and mirrors `DependencyCheckOptions` exactly:

- `Capacity` is a limit, and `OptionsTests` documents the rule that "appsettings.json is the
  single source of truth for the limits" — the options class carries no initializer, so the
  value can only come from the shipped config file. It is validated positive alongside the
  other limits, so a `0` fails host startup rather than silently meaning "no cache".
- `Enabled` is a bool, and a bool has no "absent". A missing or misspelled env key must leave
  the tracking **on** rather than silently switch it off, so its default lives in code and gets
  the same style of test as `Dependency_checks_are_on_unless_something_explicitly_says_otherwise`.

`Enabled: false` skips the hashing as well as the accounting — the hash is the cost, so gating
one without the other would be pointless — and emits **no** `duplicate_files` measurements at
all. A dashboard then shows "no data", not "no duplicates". Stated here because the two are
easy to confuse and only one of them is true.

Nothing else is configurable. The instrument set is not opt-in per instrument; five counters
with bounded cardinality do not need a kill switch each.

## 5. Chart and the export path

Every instrument in §1 is unreadable in AKS today. OTel exports only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set (`src/ServiceDefaults/Extensions.cs`), and
`charts/eugo-docint` has no value for it — only the verbatim `extraEnv` escape hatch. This
section closes the chart half. The collector itself is EuGo-infra's work (see Non-goals).

New values, all empty by default so a release that sets none behaves exactly as today:

| Value | Env var | Empty means |
| --- | --- | --- |
| `otel.endpoint` | `OTEL_EXPORTER_OTLP_ENDPOINT` | var omitted — no export, current behaviour |
| `otel.protocol` | `OTEL_EXPORTER_OTLP_PROTOCOL` | var omitted — the exporter's own default (gRPC) |

Plus `OTEL_SERVICE_NAME`, rendered to the chart fullname **only when `otel.endpoint` is set**.
Without it every namespace reports as `DocInt.Api` and two releases cannot be told apart.

`otel.protocol` is included rather than left to `extraEnv` because the .NET exporter defaults
to gRPC while a good number of collector deployments accept only `http/protobuf`; omitting it
makes the first deployment a guaranteed round-trip.

Duplicate-tracking keys get **no** first-class values. `extraEnv` already covers a dial nobody
is expected to turn, and the chart's stated principle is that each first-class value earns its
place.

**Versioning:** chart `0.1.3` → `0.1.4`, a chart-owned **patch**, released as `chart-v0.1.4`.

The three env vars are read by ServiceDefaults, which is already in the shipped image — the
chart change requires no new image and works against every `0.1.x` build. So it is exactly the
"several chart versions per image is normal" case the `Chart.yaml` contract describes.

A minor bump to `0.2.0` would put the chart's minor ahead of the image's for the whole interval
until a `v0.2.0` tag exists, breaking the `major.minor` invariant with nothing in `chart-lint`
to catch it. **When the image release carrying these counters is cut, that release moves both
image and chart to `0.2.0` together** — that is the release's job, not this branch's. This
branch leaves `appVersion` alone; it is CI-stamped and never hand-edited.

CI's existing `chart-lint` job extends to assert that the three env vars render when
`otel.endpoint` is set and are absent when it is not.

## 6. Invariants and failure handling

- **Cardinality is closed.** The only tags any instrument may carry are `kind`, `outcome`,
  `reason` and `scope`, each drawn from the fixed vocabularies in §1. Never a filename, never a
  hash, never an exception message. Total series across all six instruments is bounded at 136.
- **Metrics never fail a request.** Every emission is a `Counter.Add` or `Histogram.Record`,
  which do not throw. `XxHash64.HashToUInt64` over a `byte[]` cannot throw. The tracker holds
  one non-reentrant lock and calls nothing while holding it.
- **No document content anywhere new.** The redaction rule is unchanged: filenames and sizes in
  logs, content never. No new log field is added; the only change to the existing log line is
  that `sizeBytes` becomes accurate for over-cap files (§2.5).
- **Nothing observable changes on the wire.** No response body, status code, header or OpenAPI
  document is affected by anything in this spec.

## 7. Testing

All offline. No Azure, no live suite, no new golden fixtures.

### `DuplicateFileTrackerTests` — new, pure unit tests, no host

- An empty batch reports `(0, 0)`.
- A batch of *n* identical hashes reports `WithinRequest = n − 1`, `AcrossRequests = 0`.
- The same hash in a second call reports `AcrossRequests = 1` — and the two scopes never
  double-count a hash that is both repeated within the batch and previously seen.
- Eviction: with a small `Capacity`, a hash pushed out by later inserts is no longer recognised.

### `TelemetryTests` — extended, through the real HTTP pipeline

Using `MetricCollector<long>` / `MetricCollector<double>` against `DocIntTelemetry.MeterName`,
which the existing `pages_processed` test already establishes as the pattern.

- **`files_processed`** — a mixed batch (a golden XLSX plus the corrupt fixture); assert the
  per-`kind`/`outcome` counts. This is the test that pins down "failures still count".
- **`bytes_processed`** — assert the total equals the sum of the posted file lengths.
- **`bytes_processed` for an over-cap file** — a factory with `DocInt:MaxFileBytes` set low,
  posting a golden above it; assert the observed size is counted rather than `0`. This is the
  regression test for the `BufferAsync` change in §2.5, and it avoids needing a 50 MB fixture.
- **`file_duration`** — assert one measurement per file with the expected tags. Assert nothing
  about magnitude: it is timing-dependent and would flake.
- **`duplicate_files` `scope=request`** — the same golden twice in one request → 1.
- **`duplicate_files` `scope=pod`** — the same golden in two successive requests against one
  factory (one process = one pod) → a measurement of `0` on the first, `1` on the second. Both
  requests emit, per §3.5; the assertion is on the values, not on measurement count.
- **`duplicate_files` with `Enabled: false`** — a factory that sets the key off; posting the
  same golden twice yields *no measurements at all* on either scope. This is the assertion that
  separates "no data" from "zero", and it covers the flag from the call site, where it lives.
- **`rejected_requests`** — a `[Theory]` over several reasons: a non-`multipart` body
  (`not_multipart`), more than `MaxFilesPerRequest` parts (`too_many_files`), and an unparseable
  hints part (`hints_invalid`).

  The `hints_invalid` case **must post a valid file part alongside the bad hints.**
  `ReadAsync` checks `files.Count == 0` before it calls `HintsParser.Parse`, so a request
  carrying only a malformed `hints` part is rejected as `no_files` and the test would assert the
  wrong reason.

**The three `duplicate_files` tests each construct their own factory** rather than sharing the
class fixture. The pod cache is a singleton for the lifetime of a host, so a golden posted by an
earlier test in the same class would already be in it, and `scope=pod` would read 1 where the
test expects 0. A shared fixture makes that failure depend on test execution order — green on
one run, red on the next.

### Unchanged tests that must stay green

- The log-redaction test. Nothing new is logged, and hashes are never logged.
- The existing `pages_processed` test — that instrument and its guard are untouched.

### `OptionsTests` — extended

- The zero-limit `[Theory]` gains `DocInt:DuplicateTracking:Capacity`, proving a `0` fails host
  startup rather than silently meaning "no cache".
- `Capacity` binds from `appsettings.json` at `100000`, matching the pattern of
  `Appsettings_supplies_the_spec_defaults`.
- `new DuplicateTrackingOptions().Enabled` is `true`, the code-default rule from §4 — the same
  assertion `Dependency_checks_are_on_unless_something_explicitly_says_otherwise` makes.

### Gate

The enforced sequence, unfiltered, per `CLAUDE.md`:

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Plus `helm lint charts/eugo-docint` and
`helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml`.

## 8. Deliberately deferred

Each of these was raised and set aside; recorded so the reasoning is not re-derived.

- **Standing up a collector in EuGo-infra.** Without it §5's chart value has nothing to point
  at. This is the next piece of work if the numbers are actually wanted in the cluster.
- **`docint.dependency_up{service}`** — an observable gauge over `DependencyHealthSnapshot`,
  nearly free. Declined for this round; `/healthz` already carries the current verdict.
- **`docint.files_in_flight`** — an `UpDownCounter` against `MaxParallelism`. Declined: it
  shows saturation but never backlog depth, since files waiting for a slot are not in flight.
- **Deduplicating within a request** — skipping the engine call for a repeated file is a real
  Azure saving, but it changes response semantics and belongs in its own spec. `scope=request`
  is the measurement that would justify it.
- **An Azure-call latency histogram** — for `pdf`/`docx`/`pptx`/`html` the per-file duration
  essentially *is* the Azure call, so a separate instrument would mostly restate
  `file_duration`.
- **An output-size counter** (Markdown bytes produced). No decision depends on it today.
- **Warning counters.** Warnings are free-text strings with no code taxonomy; a counter would
  need one invented first, which is a larger change than it looks.

### Found during implementation, deliberately not fixed

Raised by the per-task reviews, adjudicated as Minor, and deferred rather than dropped.
Recorded here because the review ledger they came from was scratch. None blocks merge. They
share one shape: a test that passes for a weaker implementation than the one shipped.

- **`TelemetryTests`' `MixedBatch` fixtures are all under the size cap**, so `SizeBytes` equals
  `Bytes.Length` for every one of them and a `bytes_processed` implementation that read
  `Bytes.Length` directly would pass identically. The code is right; the §2.5 coupling it
  depends on is simply unasserted through the pipeline. Adding one over-cap fixture to that test
  class would close this and the next item at once.
- **`Under_cap_file_reports_its_own_length` would also pass against a naive
  `SizeBytes => Bytes.Length`.** Correctly framed as a control for the over-cap test beside it —
  noted so it is not mistaken for the assertion that pins §2.5.
- **`A_hash_evicted_by_later_inserts_is_no_longer_recognised` asserts `(0, 0)` on an *enabled*
  tracker**, which after Task 5's self-gating fix is coincidentally the same shape a *disabled*
  tracker returns. If `DuplicateTrackingOptions.Enabled`'s code default ever flipped to false,
  this one test would pass vacuously. Sibling tests assert non-zero values and would fail loudly,
  so exposure is narrow.
- **`OptionsTests`' new `Capacity` case checks only `Assert.Contains("positive", …)`** — a
  substring all five validators share — so it does not prove the `DuplicateTracking` validator
  specifically fired. Inherited from the pre-existing theory pattern rather than new here.
- **`TelemetryTests.cs`'s `Assert.All` over `kind ∈ {xlsx, unknown}` would pass even if all
  three measurements carried one value.** The sum assertion above it already pins the total.
- **`ExtractionService`'s two edited lines (the trace tag and the log field) have no direct
  assertion.** Standing up a log-capture harness for one field is disproportionate to the risk.
