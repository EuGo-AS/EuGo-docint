# Admission control and the autoscaling signal — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bound the bytes a pod holds in flight so overload sheds with a 503 instead of an OOMKill, and point the HPA at the memory that bound makes meaningful.

**Architecture:** An endpoint filter on `/v1/extract` reserves budget from `Content-Length` before `MultipartExtractRequestReader` buffers anything, waits up to a configured window, and sheds with `503 + Retry-After` if the budget never frees. A new `MaxRequestFileBytes` caps the sum of file parts, which drops Kestrel's accept ceiling from 1.56 GiB to ~201 MiB. With peak memory now `baseline + budget`, the HPA gains a memory `AverageValue` metric alongside the existing CPU one.

**Tech Stack:** .NET 10 · ASP.NET Core minimal API · `System.Threading.RateLimiting.ConcurrencyLimiter` (ships in the `Microsoft.AspNetCore.App` shared framework — **no `PackageReference` needed**) · Helm · xUnit + `MetricCollector`

**Spec:** `docs/superpowers/specs/2026-08-08-docint-admission-control-and-autoscaling-signal-design.md`

**Branch:** `feat/admission-gate`, already cut from `main`. The spec commit `f550ba8` is its only commit.

## Global Constraints

- `net10.0`, `Nullable` and `ImplicitUsings` enabled. Do not add `<PackageReference>` entries — every type this plan uses is in the shared framework already.
- The merge gate, in this exact order, unfiltered, from the repo root:
  `dotnet restore src/DocInt.slnx` → `dotnet build --no-restore src/DocInt.slnx` → `dotnet test --no-build src/DocInt.slnx`
- `appsettings.json` is the **single source of truth** for shipped defaults. Options classes get **no property initializers** except `bool Enabled`, which carries `= true` because a bool has no "absent".
- The chart **never restates a shipped number**. Every `docint.*` value defaults to `""`, and empty omits the env var. `0` and negatives must reach the pod and fail `ValidateOnStart` — never use Helm's `with` on a numeric value, because `with` treats `0` as empty. The same trap applies to `false`.
- **No document content in logs or metric tags.** Filenames and sizes are fine.
- Per-file failures stay inside the 200. The only new request-level codes are the 400 in Task 2 and the 503 in Task 5.
- `chart-lint` in CI is red for reasons unrelated to this work only if something regresses — it was made green on `main` by commit `797bde6`. Verify chart changes locally with `helm template` (helm 4.1.4 is installed) **and** keep CI's assertions in step.
- Commit after every task. Never merge red.

## File Structure

| File | Responsibility | Task |
| --- | --- | --- |
| `src/DocInt.Api/Configuration/DocIntOptions.cs` | `MaxRequestFileBytes`, re-based `MaxRequestBytes`, `AdmissionOptions`, all validation | 1, 3 |
| `src/DocInt.Api/appsettings.json` | shipped defaults for both | 1, 3 |
| `src/DocInt.Api/Validation/HintsParser.cs` | `RejectReasons.RequestFilesTooLarge` (the vocabulary lives here already) | 2 |
| `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs` | running file-byte sum → 400 | 2 |
| `src/DocInt.Api/Admission/RequestAdmissionGate.cs` | the gate, `AdmissionLease`, `ShedReasons` | 4 |
| `src/DocInt.Api/Admission/AdmissionFilter.cs` | endpoint filter: reserve → 503 or continue | 5 |
| `src/DocInt.Api/Telemetry/DocIntTelemetry.cs` | `docint.shed_requests` | 5 |
| `src/DocInt.Api/Api/ExtractEndpoint.cs` | filter registration + `ProducesProblem(503)` | 5 |
| `src/DocInt.Api/Program.cs` | DI registration for the gate | 5 |
| `charts/eugo-docint/**` | memory metric, scaleDown behavior, new values, corrected comments | 6 |
| `tests/DocInt.Tests/TestSupport.cs` | `TestOptions.DocInt` gains `maxRequestFileBytes` | 1 |
| `tests/DocInt.Tests/AdmissionGateTests.cs` | unit tests for the gate | 4 |

---

### Task 1: `MaxRequestFileBytes` and the shrunken Kestrel ceiling

`MaxRequestBytes` currently derives from `MaxFileBytes × MaxFilesPerRequest`, which is 1.56 GiB — 78% of the container's whole memory limit. This task re-bases it on a new explicit cap.

**Files:**
- Modify: `src/DocInt.Api/Configuration/DocIntOptions.cs:10-17` (property + derived value) and `:150-155` (validation)
- Modify: `src/DocInt.Api/appsettings.json:2-6`
- Modify: `tests/DocInt.Tests/TestSupport.cs:17-34`
- Test: `tests/DocInt.Tests/OptionsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `DocIntOptions.MaxRequestFileBytes` (`long`), and `MaxRequestBytes => MaxRequestFileBytes + 1_048_576`. Task 2 reads `MaxRequestFileBytes`; Tasks 3–5 read `MaxRequestBytes`.

- [ ] **Step 1: Write the failing tests**

In `tests/DocInt.Tests/OptionsTests.cs`, **replace** the existing `MaxRequestBytes` assertion on line 22:

```csharp
        Assert.Equal(52_428_800L * 32 + 1_048_576, o.MaxRequestBytes);
```

with these two lines, and add the new limit to the same test:

```csharp
        Assert.Equal(209_715_200, o.MaxRequestFileBytes);
        Assert.Equal(209_715_200L + 1_048_576, o.MaxRequestBytes);
```

Add `"DocInt:MaxRequestFileBytes"` as a new `[InlineData]` case on the existing `Zero_limit_fails_host_startup` theory (after `[InlineData("DocInt:MaxParallelism")]`).

Then add this new test at the end of the class, before the closing brace:

```csharp
    // A request-total cap below the per-file cap makes a single maximum-size file inadmissible:
    // the file passes its own check and then the request fails, which reads as a server bug from
    // the caller's side. Rejected at boot instead.
    [Fact]
    public void Request_total_below_the_per_file_cap_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate(
            ("DocInt:MaxRequestFileBytes", "1024")));
        Assert.Contains("MaxRequestFileBytes", ex.Message);

        // Control: the shipped values satisfy it, so it is the 1024 that fails and not the rule.
        Validate();
    }
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DocIntOptions` has no `MaxRequestFileBytes`. That is the failure for this step; the tests cannot run until the property exists.

- [ ] **Step 3: Add the property and re-base the derived value**

In `src/DocInt.Api/Configuration/DocIntOptions.cs`, add the property after `MaxParallelism` (line 13) and replace the `MaxRequestBytes` expression:

```csharp
    public int MaxParallelism { get; set; }

    /// <summary>
    /// Cap on the sum of accepted file bytes in one request. Distinct from MaxFileBytes x
    /// MaxFilesPerRequest, which is the pathological product of the two (32 x 50 MiB = 1.56 GiB)
    /// and was what MaxRequestBytes used to be — a body 78% the size of the container's entire
    /// memory limit, which Kestrel was configured to accept. Both per-file caps are unchanged;
    /// this bounds their combination.
    /// </summary>
    public long MaxRequestFileBytes { get; set; }

    /// <summary>Request-level cap: accepted file bytes plus multipart framing and the hints part.</summary>
    public long MaxRequestBytes => MaxRequestFileBytes + 1_048_576;
```

In the same file, extend the `DocIntOptions` validator (line 152) — add `MaxRequestFileBytes` to the positivity rule and add the ordering rule:

```csharp
        builder.Services.AddOptions<DocIntOptions>()
            .Bind(builder.Configuration.GetSection(DocIntOptions.SectionName))
            .Validate(o => o.MaxFileBytes > 0 && o.MaxFilesPerRequest > 0
                        && o.PerFileTimeoutSeconds > 0 && o.MaxParallelism > 0
                        && o.MaxRequestFileBytes > 0,
                "DocInt options must all be positive")
            // A request-total under the per-file cap makes one maximum-size file inadmissible.
            .Validate(o => o.MaxRequestFileBytes >= o.MaxFileBytes,
                $"{DocIntOptions.SectionName}:MaxRequestFileBytes must be at least MaxFileBytes, "
                + "or a single maximum-size file can never be accepted")
            .ValidateOnStart();
```

In `src/DocInt.Api/appsettings.json`, add the key after `"MaxFilesPerRequest": 32,`:

```json
    // Cap on the SUM of accepted file bytes in one request, and so on what Kestrel will accept
    // (this + 1 MiB of framing/hints slack). MaxFileBytes and MaxFilesPerRequest are unchanged
    // and still apply; this bounds their product, which at 32 x 50 MiB would be 1.56 GiB against
    // a 2 GiB pod. Over this is a request-level 400, not a per-file error.
    "MaxRequestFileBytes": 209715200,
```

In `tests/DocInt.Tests/TestSupport.cs`, add the parameter to **both** methods (a deliberately non-shipped value, per the class comment):

```csharp
    public static DocIntOptions DocInt(
        long maxFileBytes = 1_048_576,
        int maxFilesPerRequest = 8,
        int perFileTimeoutSeconds = 30,
        int maxParallelism = 2,
        long maxRequestFileBytes = 4_194_304) => new()
        {
            MaxFileBytes = maxFileBytes,
            MaxFilesPerRequest = maxFilesPerRequest,
            PerFileTimeoutSeconds = perFileTimeoutSeconds,
            MaxParallelism = maxParallelism,
            MaxRequestFileBytes = maxRequestFileBytes
        };

    public static IOptions<DocIntOptions> Wrapped(
        long maxFileBytes = 1_048_576,
        int maxFilesPerRequest = 8,
        int perFileTimeoutSeconds = 30,
        int maxParallelism = 2,
        long maxRequestFileBytes = 4_194_304) =>
        Options.Create(DocInt(maxFileBytes, maxFilesPerRequest, perFileTimeoutSeconds,
            maxParallelism, maxRequestFileBytes));
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~OptionsTests"
```

Expected: PASS. If `MultipartReaderTests` fails, the `TestOptions` default was missed — without it `MaxRequestFileBytes` is 0 and `MaxRequestBytes` collapses to 1 MiB.

- [ ] **Step 5: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add src/DocInt.Api/Configuration/DocIntOptions.cs src/DocInt.Api/appsettings.json tests/DocInt.Tests/TestSupport.cs tests/DocInt.Tests/OptionsTests.cs
git commit -m "Cap the sum of file bytes per request, not just the product of the caps"
```

---

### Task 2: Enforce the request-total cap while reading

`Content-Length` is absent under chunked transfer encoding and can simply be wrong, so the declared-size check is not enough on its own.

**Files:**
- Modify: `src/DocInt.Api/Validation/HintsParser.cs:21-31` (the `RejectReasons` vocabulary)
- Modify: `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs:37-83`
- Test: `tests/DocInt.Tests/MultipartReaderTests.cs`

**Interfaces:**
- Consumes: `DocIntOptions.MaxRequestFileBytes` from Task 1.
- Produces: `RejectReasons.RequestFilesTooLarge` (`"request_files_too_large"`), the ninth value in the closed `docint.rejected_requests` vocabulary.

- [ ] **Step 1: Write the failing test**

Add to `tests/DocInt.Tests/MultipartReaderTests.cs`:

```csharp
    // Two files, each inside MaxFileBytes, whose sum is over MaxRequestFileBytes. Neither is a
    // per-file too_large, so without a running sum the pair sails through and the pod buffers
    // more than the cap allows. Checked while reading rather than only against Content-Length,
    // which chunked encoding omits entirely.
    [Fact]
    public async Task Files_summing_over_the_request_total_is_a_request_level_rejection()
    {
        using var form = Multipart.Form(
            ("a.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"),
            ("b.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"));
        var request = await RequestOf(form);
        var reader = Reader(TestOptions.DocInt(maxFileBytes: 4096, maxRequestFileBytes: 6000));

        var ex = await Assert.ThrowsAsync<BadExtractRequestException>(
            () => reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(RejectReasons.RequestFilesTooLarge, ex.Reason);
    }

    // Control: the same two files under the cap are accepted, so it is the sum that rejects and
    // not the per-file cap or the declared-size check ahead of it.
    [Fact]
    public async Task Files_summing_under_the_request_total_are_accepted()
    {
        using var form = Multipart.Form(
            ("a.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"),
            ("b.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"));
        var request = await RequestOf(form);
        var reader = Reader(TestOptions.DocInt(maxFileBytes: 4096, maxRequestFileBytes: 100_000));

        var files = await reader.ReadAsync(request, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Null(f.Error));
    }

    private static byte[] Pad(byte[] prefix, int total)
    {
        var padded = new byte[total];
        prefix.CopyTo(padded, 0);
        return padded;
    }
```

`Reader(DocIntOptions?)` and `RequestOf(MultipartFormDataContent)` are the file's existing private
helpers (`tests/DocInt.Tests/MultipartReaderTests.cs:11` and `:14`) — use them, do not add new ones.
Note `Reader` takes a bare `DocIntOptions`, so pass `TestOptions.DocInt(...)`, not `.Wrapped(...)`.

The arithmetic these two rely on: `RequestOf` sets `ContentLength` to the real body length (~8.2 kB),
and `MaxRequestBytes` is `6000 + 1 MiB`, so the **declared-size check passes** and the running sum is
genuinely what rejects. Without that headroom the test would pass for the wrong reason.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~Files_summing_over_the_request_total"
```

Expected: FAIL — compile error on `RejectReasons.RequestFilesTooLarge`, which does not exist yet.

- [ ] **Step 3: Add the reason and the running sum**

In `src/DocInt.Api/Validation/HintsParser.cs`, add to `RejectReasons` after `TooManyFiles`:

```csharp
    public const string RequestFilesTooLarge = "request_files_too_large";
```

In `src/DocInt.Api/Validation/MultipartExtractRequestReader.cs`, declare the accumulator beside `files` (line 37):

```csharp
        var files = new List<FileItem>();
        // Accumulated across parts, so two files that each pass MaxFileBytes cannot together
        // exceed what the pod agreed to hold. Counts observed bytes, not retained ones: an
        // over-cap file retains nothing but still arrived and still occupied the socket.
        long acceptedBytes = 0;
        string? hintsJson = null;
```

Then, immediately after the `var (bytes, observed, tooLarge) = await BufferAsync(...)` line (line 55), add:

```csharp
                    acceptedBytes += observed;
                    if (acceptedBytes > _options.MaxRequestFileBytes)
                        throw new BadExtractRequestException(RejectReasons.RequestFilesTooLarge,
                            $"file parts total more than {_options.MaxRequestFileBytes} bytes");
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~MultipartReaderTests"
```

Expected: PASS, all of them.

- [ ] **Step 5: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add src/DocInt.Api/Validation/HintsParser.cs src/DocInt.Api/Validation/MultipartExtractRequestReader.cs tests/DocInt.Tests/MultipartReaderTests.cs
git commit -m "Reject a request whose file parts exceed the request-total cap"
```

---

### Task 3: `AdmissionOptions` and the rule that makes over-budget unreachable

`BudgetBytes >= MaxRequestBytes` is the load-bearing rule: Kestrel already refuses anything larger than `MaxRequestBytes`, so with this rule every request that reaches the gate necessarily fits the budget. That removes a runtime branch entirely — and it also keeps `ConcurrencyLimiter.AcquireAsync` from being handed a `permitCount` above its `PermitLimit`, which throws.

**Files:**
- Modify: `src/DocInt.Api/Configuration/DocIntOptions.cs` (new class + registration)
- Modify: `src/DocInt.Api/appsettings.json`
- Test: `tests/DocInt.Tests/OptionsTests.cs`

**Interfaces:**
- Consumes: `DocIntOptions.MaxRequestBytes` from Task 1.
- Produces: `AdmissionOptions` with `SectionName = "DocInt:Admission"`, `bool Enabled`, `long BudgetBytes`, `int QueueTimeoutSeconds`, `int RetryAfterSeconds`. Tasks 4 and 5 inject `IOptions<AdmissionOptions>`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/DocInt.Tests/OptionsTests.cs`, before the closing brace:

```csharp
    [Fact]
    public void Admission_defaults_bind_from_appsettings()
    {
        using var factory = new DocIntAppFactory();
        var o = factory.Services.GetRequiredService<IOptions<AdmissionOptions>>().Value;
        Assert.Equal(1_073_741_824, o.BudgetBytes);
        Assert.Equal(10, o.QueueTimeoutSeconds);
        Assert.Equal(5, o.RetryAfterSeconds);
    }

    // Same rule as DependencyCheckOptions and DuplicateTrackingOptions: a bool has no "absent",
    // so a missing or misspelled env key must leave the gate on rather than silently remove the
    // only thing standing between a burst and an OOMKill.
    [Fact]
    public void Admission_is_on_unless_something_explicitly_says_otherwise() =>
        Assert.True(new AdmissionOptions().Enabled);

    // The rule that makes an over-budget request impossible rather than merely handled. Kestrel
    // refuses anything above MaxRequestBytes, so a budget at least that large means every request
    // reaching the gate fits it. A release that breaks the invariant fails to boot instead of
    // discovering it as an ArgumentOutOfRangeException from the limiter on a live request.
    [Fact]
    public void Budget_below_the_largest_admissible_request_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate(
            ("DocInt:Admission:BudgetBytes", "1048576")));
        Assert.Contains("BudgetBytes", ex.Message);

        // Control: the shipped values satisfy it, so it is the 1 MiB that fails and not the rule.
        Validate();
    }
```

Also add these two cases to the existing `Zero_limit_fails_host_startup` theory:

```csharp
    [InlineData("DocInt:Admission:BudgetBytes")]
    [InlineData("DocInt:Admission:QueueTimeoutSeconds")]
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `AdmissionOptions` does not exist.

- [ ] **Step 3: Add the options class**

In `src/DocInt.Api/Configuration/DocIntOptions.cs`, add after `DuplicateTrackingOptions` (line 120):

```csharp
/// <summary>
/// The per-pod ceiling on bytes held in flight, and what happens when it is reached. Nested under
/// DocInt alongside the other knobs (DocInt__Admission__Enabled from the environment).
/// </summary>
/// <remarks>
/// This is the only thing bounding pod memory. MultipartExtractRequestReader holds every accepted
/// file's byte[] for the whole request and nothing caps concurrent requests, so without it peak
/// memory is bytes-per-request x concurrent-requests against the pod's limit. With it, peak is
/// baseline + BudgetBytes — which is also what makes memory worth autoscaling on.
/// </remarks>
public sealed class AdmissionOptions
{
    public const string SectionName = "DocInt:Admission";

    /// <summary>
    /// The off switch, carrying its default for the same reason the other three do: a bool has no
    /// "absent", and defaulting to false would turn a typo into an unprotected pod. False admits
    /// every request immediately and emits no measurements — it disables admission only, never the
    /// request limits in DocIntOptions.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializers below, matching the classes above: appsettings.json owns the shipped values.
    /// <summary>Bytes a pod will hold in flight at once, across all concurrent requests.</summary>
    public long BudgetBytes { get; set; }
    /// <summary>How long a request waits for budget before it is shed with a 503.</summary>
    public int QueueTimeoutSeconds { get; set; }
    /// <summary>Seconds advertised in the Retry-After header on that 503.</summary>
    public int RetryAfterSeconds { get; set; }
}
```

Register it in `AddDocIntOptions`, **after** the `DocIntOptions` registration so the dependency reads naturally (DI resolution order does not depend on this, but the file should):

```csharp
        builder.Services.AddOptions<AdmissionOptions>()
            .Bind(builder.Configuration.GetSection(AdmissionOptions.SectionName))
            .Validate(o => o.BudgetBytes > 0 && o.QueueTimeoutSeconds > 0 && o.RetryAfterSeconds > 0,
                $"{AdmissionOptions.SectionName} budget and timings must be positive")
            // Kestrel refuses anything above MaxRequestBytes, so a budget at least that large makes
            // an over-budget request unreachable rather than a case to handle. Without this the
            // limiter would be asked for more permits than it owns and would throw on a live request.
            .Validate<IOptions<DocIntOptions>>((o, docint) => o.BudgetBytes >= docint.Value.MaxRequestBytes,
                $"{AdmissionOptions.SectionName}:BudgetBytes must be at least "
                + "DocInt:MaxRequestBytes, or a legal request could never be admitted")
            .ValidateOnStart();
```

In `src/DocInt.Api/appsettings.json`, add after the `DuplicateTracking` block (keeping it inside `DocInt`):

```json
    // The per-pod ceiling on bytes held in flight, and the only thing bounding pod memory: the
    // reader holds every accepted file's bytes for the whole request and nothing caps concurrent
    // requests, so peak used to be bytes-per-request x concurrent-requests. Peak is now
    // baseline + BudgetBytes, which is what makes the HPA's memory metric mean anything.
    // BudgetBytes must be >= MaxRequestFileBytes + 1 MiB (enforced at boot): Kestrel already
    // refuses larger bodies, so that makes an over-budget request impossible rather than handled.
    // A request that cannot get budget within QueueTimeoutSeconds is shed with 503 + Retry-After;
    // most bursts drain well inside it and still answer 200.
    "Admission": {
      "Enabled": true,
      "BudgetBytes": 1073741824,
      "QueueTimeoutSeconds": 10,
      "RetryAfterSeconds": 5
    },
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~OptionsTests"
```

Expected: PASS. If `Validate<IOptions<DocIntOptions>>` does not compile, add `using Microsoft.Extensions.Options;` — `OptionsBuilder<T>.Validate<TDep>` needs the dependency resolvable from DI, and `IOptions<DocIntOptions>` is registered in the same method.

- [ ] **Step 5: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add src/DocInt.Api/Configuration/DocIntOptions.cs src/DocInt.Api/appsettings.json tests/DocInt.Tests/OptionsTests.cs
git commit -m "Add DocInt:Admission options with a budget-covers-the-largest-request rule"
```

---

### Task 4: `RequestAdmissionGate`

Pure unit, no wiring. The gate mirrors `EngineRouter.RouteAsync`'s linked-CTS pattern (`src/DocInt.Api/Engines/EngineRouter.cs:27-43`) to tell its own timeout apart from genuine client abandonment.

**Files:**
- Create: `src/DocInt.Api/Admission/RequestAdmissionGate.cs`
- Test: `tests/DocInt.Tests/AdmissionGateTests.cs`

**Interfaces:**
- Consumes: `AdmissionOptions` from Task 3.
- Produces:
  - `RequestAdmissionGate(IOptions<AdmissionOptions>)`, `IDisposable`
  - `Task<AdmissionLease?> AcquireAsync(long bytes, CancellationToken requestCt)` — `null` means shed
  - `sealed class AdmissionLease : IDisposable`
  - `internal static int RequestAdmissionGate.Permits(long bytes)`
  - `static class ShedReasons { public const string QueueTimeout = "queue_timeout"; }`

- [ ] **Step 1: Write the failing tests**

Create `tests/DocInt.Tests/AdmissionGateTests.cs`:

```csharp
using DocInt.Api.Admission;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class AdmissionGateTests
{
    private static RequestAdmissionGate Gate(
        long budgetBytes = 4 * 1024 * 1024, int queueTimeoutSeconds = 10, bool enabled = true) =>
        new(Options.Create(new AdmissionOptions
        {
            Enabled = enabled,
            BudgetBytes = budgetBytes,
            QueueTimeoutSeconds = queueTimeoutSeconds,
            RetryAfterSeconds = 5
        }));

    // Permits are MiB, rounded up, floored at 1: a request must always cost something, or a
    // thousand tiny requests would each reserve nothing and the budget would bound nothing.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024 * 1024, 1)]
    [InlineData(1024 * 1024 + 1, 2)]
    [InlineData(200L * 1024 * 1024, 200)]
    public void Permits_are_mebibytes_rounded_up(long bytes, int expected) =>
        Assert.Equal(expected, RequestAdmissionGate.Permits(bytes));

    [Fact]
    public async Task A_request_within_budget_is_admitted()
    {
        using var gate = Gate();
        using var lease = await gate.AcquireAsync(1024 * 1024, CancellationToken.None);
        Assert.NotNull(lease);
    }

    // The point of the whole component: while one request holds the budget, the next one waits and
    // then sheds rather than allocating alongside it.
    [Fact]
    public async Task A_second_request_over_budget_is_shed_after_the_queue_timeout()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 1);
        using var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);

        var shed = await gate.AcquireAsync(1024 * 1024, CancellationToken.None);

        Assert.Null(shed);
    }

    // ...and releasing the first lease lets the next one straight through, so the timeout above is
    // the budget being full and not the gate being broken.
    [Fact]
    public async Task Releasing_a_lease_frees_the_budget_for_the_next_request()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 10);
        var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);
        held.Dispose();

        using var next = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);

        Assert.NotNull(next);
    }

    // A client that hangs up while queued is not a shed request: there is nobody to answer and
    // nothing to report, so the cancellation propagates instead of becoming a 503. Same split as
    // EngineRouter makes between its per-file timeout and request abandonment.
    [Fact]
    public async Task A_client_disconnect_while_queued_propagates_rather_than_shedding()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 30);
        using var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        using var aborted = new CancellationTokenSource();

        var pending = gate.AcquireAsync(4 * 1024 * 1024, aborted.Token);
        await aborted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // Disabled means admit everything, including a request far past the budget. The request-level
    // limits in DocIntOptions are unaffected — this switch removes admission, not validation.
    [Fact]
    public async Task A_disabled_gate_admits_everything()
    {
        using var gate = Gate(budgetBytes: 1024 * 1024, enabled: false);
        using var lease = await gate.AcquireAsync(500L * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(lease);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DocInt.Api.Admission` does not exist.

- [ ] **Step 3: Write the gate**

Create `src/DocInt.Api/Admission/RequestAdmissionGate.cs`:

```csharp
using System.Threading.RateLimiting;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Admission;

/// <summary>The closed vocabulary behind the `reason` tag on docint.shed_requests.</summary>
public static class ShedReasons
{
    public const string QueueTimeout = "queue_timeout";
}

/// <summary>
/// Bounds the bytes a pod holds in flight. Acquisition is all-or-nothing and happens once, before
/// anything is buffered — accounting per file as it arrives would let several requests each hold
/// partial budget while waiting for more, which is hold-and-wait deadlock, and would allocate
/// before deciding.
/// </summary>
public sealed class RequestAdmissionGate : IDisposable
{
    private const long Mebibyte = 1024 * 1024;

    // Null when disabled, which is also what makes the disabled path allocation-free.
    private readonly ConcurrencyLimiter? _limiter;
    private readonly TimeSpan _queueTimeout;

    public RequestAdmissionGate(IOptions<AdmissionOptions> options)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        _queueTimeout = TimeSpan.FromSeconds(o.QueueTimeoutSeconds);
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Permits(o.BudgetBytes),
            // The wait is bounded by _queueTimeout below, not by queue depth: a depth limit would
            // shed the newest arrivals instantly while older ones still wait, which is the opposite
            // of the first-come-first-served behaviour the 503 is documented to mean.
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    /// <summary>
    /// Whole mebibytes, rounded up, floored at 1. Byte-granular permits would need a 64-bit permit
    /// count and buy nothing — this is a safety margin, not an accounting ledger. The floor matters:
    /// a zero-cost request would let unlimited tiny requests through a budget meant to bound them.
    /// </summary>
    internal static int Permits(long bytes) =>
        (int)Math.Max(1, (bytes + Mebibyte - 1) / Mebibyte);

    /// <summary>Returns null when the request was shed; throws when the caller abandoned it.</summary>
    public async Task<AdmissionLease?> AcquireAsync(long bytes, CancellationToken requestCt)
    {
        if (_limiter is null) return new AdmissionLease(null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestCt);
        cts.CancelAfter(_queueTimeout);
        try
        {
            var lease = await _limiter.AcquireAsync(Permits(bytes), cts.Token);
            if (lease.IsAcquired) return new AdmissionLease(lease);
            lease.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!requestCt.IsCancellationRequested)
        {
            // Our own queue timeout. The caller is still there, so it gets an answer.
            return null;
        }
    }

    public void Dispose() => _limiter?.Dispose();
}

/// <summary>Holds budget for the life of one request. Disposing returns it.</summary>
public sealed class AdmissionLease : IDisposable
{
    private readonly RateLimitLease? _inner;

    internal AdmissionLease(RateLimitLease? inner) => _inner = inner;

    public void Dispose() => _inner?.Dispose();
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~AdmissionGateTests"
```

Expected: PASS, all nine (four theory cases plus five facts).

- [ ] **Step 5: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add src/DocInt.Api/Admission/RequestAdmissionGate.cs tests/DocInt.Tests/AdmissionGateTests.cs
git commit -m "Add RequestAdmissionGate: a byte budget with a bounded wait"
```

---

### Task 5: Wire the gate, shed with 503, count it

**Files:**
- Create: `src/DocInt.Api/Admission/AdmissionFilter.cs`
- Modify: `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`
- Modify: `src/DocInt.Api/Api/ExtractEndpoint.cs:12-20`
- Modify: `src/DocInt.Api/Program.cs:41-43`
- Test: `tests/DocInt.Tests/ExtractContractTests.cs`, `tests/DocInt.Tests/TelemetryTests.cs`

**Interfaces:**
- Consumes: `RequestAdmissionGate`, `AdmissionLease`, `ShedReasons` (Task 4); `AdmissionOptions` (Task 3); `DocIntOptions.MaxRequestBytes` (Task 1).
- Produces: `DocIntTelemetry.ShedRequestsInstrument` (`"docint.shed_requests"`) and `DocIntTelemetry.ShedRequests` (`Counter<long>`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/DocInt.Tests/ExtractContractTests.cs`, before the closing brace:

```csharp
    // A pod whose budget is already spoken for answers 503 with a Retry-After rather than
    // buffering alongside the request that holds it and OOMKilling the whole pod. The budget here
    // is two mebibytes and the queue timeout one second, so the second request cannot fit and does
    // not wait long; in production both numbers are far larger and this path is rare.
    [Fact]
    public async Task A_saturated_pod_sheds_with_503_and_a_retry_after()
    {
        using var saturated = new SaturatedFactory();
        var client = saturated.CreateClient();
        var gate = saturated.Services.GetRequiredService<RequestAdmissionGate>();

        // Hold the entire budget out-of-band, so the assertion does not depend on racing two
        // real requests against each other.
        using var held = await gate.AcquireAsync(2 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);

        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await client.PostAsync("/v1/extract", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    // Control: the same factory with the budget free answers 200, so it is saturation that sheds
    // and not the filter rejecting everything.
    [Fact]
    public async Task The_same_pod_with_budget_free_answers_200()
    {
        using var saturated = new SaturatedFactory();
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await saturated.CreateClient().PostAsync("/v1/extract", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The numbers here are constrained by the boot rule BudgetBytes >= MaxRequestBytes, and
    // MaxRequestBytes is MaxRequestFileBytes + 1 MiB. So a 1 MiB budget cannot work at all: with
    // MaxRequestFileBytes at 1024 the ceiling is 1 049 600, which is *above* 1 MiB, and the host
    // refuses to start. 2 MiB clears it. Permits are whole mebibytes, so the budget is 2 permits
    // and holding 2 MiB takes both, leaving a kilobyte-sized request with nothing to acquire.
    private sealed class SaturatedFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:Admission:BudgetBytes", "2097152");
            builder.UseSetting("DocInt:Admission:QueueTimeoutSeconds", "1");
            builder.UseSetting("DocInt:MaxRequestFileBytes", "1024");
            builder.UseSetting("DocInt:MaxFileBytes", "1024");
            base.ConfigureWebHost(builder);
        }
    }
```

Add the `using` directives this needs to the top of the file if absent: `using DocInt.Api.Admission;` and `using Microsoft.Extensions.DependencyInjection;`.

Add to `tests/DocInt.Tests/TelemetryTests.cs`, before the closing brace:

```csharp
    // A shed request is not a rejected one: docint.rejected_requests is the 400 vocabulary, and a
    // 503 is a well-formed request the pod declined to start. Separate instruments, so a dashboard
    // can tell "the caller sent nonsense" from "we ran out of room".
    [Fact]
    public async Task A_shed_request_counts_on_its_own_instrument_with_a_reason()
    {
        using var saturated = new ShedFactory();
        var meterFactory = saturated.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.ShedRequestsInstrument);
        var gate = saturated.Services.GetRequiredService<RequestAdmissionGate>();
        using var held = await gate.AcquireAsync(2 * 1024 * 1024, CancellationToken.None);

        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await saturated.CreateClient().PostAsync("/v1/extract", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(ShedReasons.QueueTimeout, measurement.Tags["reason"]);
    }

    // Same numbers as ExtractContractTests.SaturatedFactory, and for the same reason: BudgetBytes
    // must clear MaxRequestFileBytes + 1 MiB or the host refuses to boot.
    private sealed class ShedFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:Admission:BudgetBytes", "2097152");
            builder.UseSetting("DocInt:Admission:QueueTimeoutSeconds", "1");
            builder.UseSetting("DocInt:MaxRequestFileBytes", "1024");
            builder.UseSetting("DocInt:MaxFileBytes", "1024");
            base.ConfigureWebHost(builder);
        }
    }
```

Add `using DocInt.Api.Admission;` to that file's directives.

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DocIntTelemetry.ShedRequestsInstrument` does not exist, and `RequestAdmissionGate` is not registered in DI.

- [ ] **Step 3: Add the instrument**

In `src/DocInt.Api/Telemetry/DocIntTelemetry.cs`, add the constant beside the others (after line 15):

```csharp
    public const string ShedRequestsInstrument = "docint.shed_requests";
```

In the constructor, after the `DuplicateFiles` assignment:

```csharp
        ShedRequests = _meter.CreateCounter<long>(ShedRequestsInstrument, unit: "requests",
            description: "Requests shed because the pod's in-flight byte budget stayed full for "
                + "the whole queue window, by reason. Distinct from docint.rejected_requests, "
                + "which counts malformed requests (400): a shed request is well-formed and "
                + "retryable");
```

And the property, after `DuplicateFiles`:

```csharp
    public Counter<long> ShedRequests { get; }
```

- [ ] **Step 4: Add the filter**

Create `src/DocInt.Api/Admission/AdmissionFilter.cs`:

```csharp
using System.Globalization;
using DocInt.Api.Configuration;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Admission;

/// <summary>
/// Reserves in-flight budget before the body is read. Sits on /v1/extract as an endpoint filter
/// rather than as middleware so it covers exactly the route that buffers — /healthz and /alive
/// must answer under saturation, which is when they matter most.
/// </summary>
public sealed class AdmissionFilter(
    RequestAdmissionGate gate,
    IOptions<DocIntOptions> docint,
    IOptions<AdmissionOptions> admission,
    DocIntTelemetry telemetry) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ceiling = docint.Value.MaxRequestBytes;
        // Clamped, and this is load-bearing: the filter runs before Kestrel reads the body, so a
        // request declaring far more than MaxRequestBytes reaches here unfiltered. Unclamped, its
        // permit count would exceed the limiter's own and AcquireAsync would throw. The reader's
        // Content-Length check still turns it into the 400 it always was.
        var reserve = Math.Min(http.Request.ContentLength ?? ceiling, ceiling);

        using var lease = await gate.AcquireAsync(reserve, http.RequestAborted);
        if (lease is null)
        {
            telemetry.ShedRequests.Add(1,
                new KeyValuePair<string, object?>("reason", ShedReasons.QueueTimeout));
            http.Response.Headers.RetryAfter =
                admission.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                title: "Service saturated",
                detail: "The pod's in-flight byte budget is full. Retry shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return await next(context);
    }
}
```

- [ ] **Step 5: Register the gate and the filter**

In `src/DocInt.Api/Program.cs`, add beside the other singletons (after `builder.Services.AddSingleton<ExtractionService>();`):

```csharp
    builder.Services.AddSingleton<RequestAdmissionGate>();
```

and add `using DocInt.Api.Admission;` to the file's directives.

In `src/DocInt.Api/Api/ExtractEndpoint.cs`, add the filter and the documented status code to the `MapPost` chain:

```csharp
        app.MapPost("/v1/extract", Handle)
            .AddEndpointFilter<AdmissionFilter>()
            .WithName("Extract")
            .WithSummary("Extract Markdown, typed tables and image descriptions from documents")
            .WithDescription("multipart/form-data: N file parts named 'files'; optional 'hints' part "
                + "with JSON {\"<filename>\":{\"purpose\":\"bom|photo\"}}. Well-formed requests always "
                + "return 200 with per-file success or error, unless the pod's in-flight byte budget "
                + "is full, which is a retryable 503.")
            .Produces<ExtractResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
```

and add `using DocInt.Api.Admission;` to that file's directives.

- [ ] **Step 6: Run the tests**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~ExtractContractTests|FullyQualifiedName~TelemetryTests"
```

Expected: PASS. The gate is left **enabled** in `DocIntAppFactory` on purpose — a 1 GiB budget never blocks kilobyte fixtures, so every other test is unaffected while the wiring gets real coverage. Do **not** add an `Admission:Enabled=false` blank to `DocIntAppFactory`; that would make these tests the only ones exercising the filter.

- [ ] **Step 7: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add src/DocInt.Api/Admission/AdmissionFilter.cs src/DocInt.Api/Telemetry/DocIntTelemetry.cs src/DocInt.Api/Api/ExtractEndpoint.cs src/DocInt.Api/Program.cs tests/DocInt.Tests/ExtractContractTests.cs tests/DocInt.Tests/TelemetryTests.cs
git commit -m "Shed a saturated request with 503 + Retry-After instead of buffering into an OOMKill"
```

---

### Task 6: The autoscaling signal and the chart

**Files:**
- Modify: `charts/eugo-docint/templates/hpa.yaml`
- Modify: `charts/eugo-docint/templates/deployment.yaml`
- Modify: `charts/eugo-docint/values.yaml`
- Modify: `charts/eugo-docint/Chart.yaml`
- Modify: `.github/workflows/ci.yml` (the `chart-lint` job)
- Modify: `docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md` §3, §4

**Interfaces:**
- Consumes: the `DocInt__*` env names from Tasks 1 and 3.
- Produces: values `autoscaling.targetMemoryAverageValue`, `autoscaling.scaleDownStabilizationSeconds`, `docint.maxRequestFileBytes`, `docint.admission.{enabled,budgetBytes,queueTimeoutSeconds,retryAfterSeconds}`.

- [ ] **Step 1: Write the failing checks**

`chart-lint` is the harness. Append to the `chart-lint` job in `.github/workflows/ci.yml`, after the OTEL step:

```yaml
      # The CPU metric alone never fires on the Azure-bound path -- the pod is blocked on I/O while
      # memory climbs -- so scale-out has to key on memory too. Both halves are asserted: the metric
      # renders with the configured value, and blanking it removes only the memory metric, which is
      # the documented back-out if GC retention pins the replica count.
      - name: hpa keys on memory as well as cpu
        run: |
          render() { helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint "$@"; }
          render > hpa-on.yaml
          grep -q 'averageValue: 900Mi' hpa-on.yaml
          grep -q 'stabilizationWindowSeconds: 600' hpa-on.yaml
          render --set autoscaling.targetMemoryAverageValue="" > hpa-off.yaml
          ! grep -q 'averageValue' hpa-off.yaml
          grep -q 'averageUtilization: 70' hpa-off.yaml
      # Same omit-when-empty contract as the other DocInt limits: 0 and false must reach the pod and
      # fail ValidateOnStart, only an empty value means "unset". Two of the five are asserted because
      # the is-set test is shared -- and `enabled` separately, because Helm's `with` treats false as
      # empty exactly as it treats 0.
      - name: admission limits reach the pod, unset ones do not
        run: |
          render() { helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint "$@"; }
          render --set docint.admission.budgetBytes=0 > adm-zero.yaml
          grep -A1 'DocInt__Admission__BudgetBytes' adm-zero.yaml > adm-matched.txt
          grep -q 'value: "0"' adm-matched.txt
          render --set docint.admission.enabled=false > adm-false.yaml
          grep -A1 'DocInt__Admission__Enabled' adm-false.yaml > adm-enabled.txt
          grep -q 'value: "false"' adm-enabled.txt
          render --set docint.maxRequestFileBytes=0 > adm-total.yaml
          grep -A1 'DocInt__MaxRequestFileBytes' adm-total.yaml > adm-total-matched.txt
          grep -q 'value: "0"' adm-total-matched.txt
          render > adm-default.yaml
          ! grep -q 'DocInt__' adm-default.yaml
```

- [ ] **Step 2: Run them and watch them fail**

```bash
helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint | grep averageValue
```

Expected: no output, exit 1 — the HPA has only the CPU metric.

- [ ] **Step 3: Add the values**

In `charts/eugo-docint/values.yaml`, replace the `autoscaling:` block:

```yaml
autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 6
  # CPU alone never fires on the dominant path: PDF/DOCX/PPTX/HTML/image work is an Azure round
  # trip, so the pod blocks on I/O while memory climbs. Kept anyway -- XLSX runs through the
  # synchronous SpreadsheetEngine and is the one CPU-bound path. autoscaling/v2 takes the max of
  # the two recommendations.
  targetCPUUtilizationPercentage: 70
  # Absolute, not a percentage: Utilization measures against requests.memory (512Mi), which is a
  # scheduling hint rather than the real ceiling, so a pod holding 900Mi would read as 176% and peg
  # the HPA at maxReplicas immediately. 900Mi is baseline (~200Mi) plus about two thirds of the
  # 1 GiB admission budget, so it fires before saturation with headroom under the 2Gi limit.
  # It is a considered starting value, not a derived one -- see the design spec S7/S8 -- and empty
  # omits the memory metric entirely, which is the documented back-out.
  targetMemoryAverageValue: 900Mi
  # Memory is sticky: the pod runs Server GC with no CPU limit, so heaps follow the node's core
  # count and the working set decays slowly after a burst. This keeps the HPA from holding a high
  # replica count on a reading that is already stale. Empty omits the behavior block.
  scaleDownStabilizationSeconds: 600
```

In the same file, extend the `docint:` block and **correct the `resources` comment**:

```yaml
  # Files processed concurrently within one request -- raise with the memory limit, not alone
  maxParallelism: ""
  # Cap on the SUM of file bytes in one request; over it is a request-level 400. Also sets what
  # Kestrel accepts (this + 1 MiB), so lowering it lowers the pod's worst-case buffered payload.
  maxRequestFileBytes: ""
  # The per-pod ceiling on bytes held in flight, and what happens when it fills. budgetBytes must
  # be at least maxRequestFileBytes + 1 MiB or the pod fails to boot -- deliberately, since a
  # budget under the largest admissible request could never serve it.
  admission:
    # false admits every request immediately; the request-level limits above still apply
    enabled: ""
    budgetBytes: ""
    queueTimeoutSeconds: ""
    retryAfterSeconds: ""
```

```yaml
resources:
  requests:
    cpu: 250m
    memory: 512Mi
  # No CPU limit: throttling hurts latency more than it helps.
  # Memory is sized for the admission budget, which is the only thing bounding it: peak is
  # roughly baseline + DocInt:Admission:BudgetBytes (1 GiB shipped), because the reader holds
  # every accepted file's bytes for the whole request. An earlier version of this comment said
  # "<=50 MB/file, 4 in flight" -- that 4 is MaxParallelism, which is per request, and requests
  # themselves were unbounded, so the real peak was bytes-per-request x concurrent-requests.
  limits:
    memory: 2Gi
```

- [ ] **Step 4: Render them**

In `charts/eugo-docint/templates/hpa.yaml`, replace the `metrics:` section and append the behavior block:

```yaml
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
    {{- with .Values.autoscaling.targetMemoryAverageValue }}
    - type: Resource
      resource:
        name: memory
        target:
          type: AverageValue
          averageValue: {{ . }}
    {{- end }}
  {{- with .Values.autoscaling.scaleDownStabilizationSeconds }}
  behavior:
    scaleDown:
      stabilizationWindowSeconds: {{ . }}
  {{- end }}
{{- end }}
```

(The trailing `{{- end }}` is the existing `if .Values.autoscaling.enabled` — do not add a second one.)

In `charts/eugo-docint/templates/deployment.yaml`, add the three numeric admission keys to the existing `$limits` dict, so they inherit the is-set test rather than growing a second one:

```yaml
          {{- $limits := dict
                "MaxFileBytes" .Values.docint.maxFileBytes
                "MaxFilesPerRequest" .Values.docint.maxFilesPerRequest
                "PerFileTimeoutSeconds" .Values.docint.perFileTimeoutSeconds
                "MaxParallelism" .Values.docint.maxParallelism
                "MaxRequestFileBytes" .Values.docint.maxRequestFileBytes
                "Admission__BudgetBytes" .Values.docint.admission.budgetBytes
                "Admission__QueueTimeoutSeconds" .Values.docint.admission.queueTimeoutSeconds
                "Admission__RetryAfterSeconds" .Values.docint.admission.retryAfterSeconds }}
```

Immediately after the `range` block that renders `$limits`, add the boolean, which needs its own
test for the same reason the numbers do — `with` treats `false` as empty exactly as it treats `0`:

```yaml
          {{- /* Not `with`: false is "empty" to Helm, so --set docint.admission.enabled=false
                 would omit the var and leave the gate silently ON -- the opposite of what the
                 operator asked for. Only nil and "" mean unset. */}}
          {{- if not (kindIs "invalid" .Values.docint.admission.enabled) }}
          {{- if ne (toString .Values.docint.admission.enabled) "" }}
          {{- $env = append $env (dict "name" "DocInt__Admission__Enabled" "value" (toString .Values.docint.admission.enabled)) }}
          {{- end }}
          {{- end }}
```

- [ ] **Step 5: Bump the chart version**

In `charts/eugo-docint/Chart.yaml`, change `version: 0.1.4` to `version: 0.1.5`. Leave `appVersion` alone — CI stamps it at package time.

A patch, not a minor: the new env vars are read by the image built from this branch, and the `major.minor` invariant ties the chart to the image's `0.1.x` line. The release that ships the admission gate moves both to `0.2.0` together.

- [ ] **Step 6: Correct the chart design spec**

In `docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md`:

- §3, replace the bullet `Memory limit is deliberately generous: requests buffer fully in memory (≤50 MB/file, `MaxParallelism` 4 by default).` with:

```markdown
- Memory limit is sized for the admission budget, which is what bounds it: peak is roughly
  baseline + `DocInt:Admission:BudgetBytes`. **Corrected 2026-08-08:** this bullet previously read
  "≤50 MB/file, `MaxParallelism` 4 by default", implying a 200 MB per-pod bound. `MaxParallelism`
  is per *request*, and until the admission gate landed nothing capped concurrent requests, so the
  real peak was bytes-per-request × concurrent-requests. See
  `2026-08-08-docint-admission-control-and-autoscaling-signal-design.md` §1.
```

- §4, in the writable-`/tmp` bullet, replace `bounding node ephemeral storage under load (≤50 MB × 4 in flight per pod) is an open follow-up` with:

```markdown
  bounding node ephemeral storage under load is an open follow-up (the earlier "≤50 MB × 4 in
  flight per pod" figure was wrong for the reason given in §3; OpenXML spill scales with
  concurrent XLSX files, which the admission budget now bounds)
```

- [ ] **Step 7: Verify locally — every chart-lint assertion, not just the new ones**

```bash
helm lint charts/eugo-docint
helm template ci charts/eugo-docint -f charts/eugo-docint/ci/test-values.yaml > /dev/null
R="helm template ci charts/eugo-docint --set image.repository=r.example/eugo-docint"
$R > on.yaml && grep -q 'averageValue: 900Mi' on.yaml && grep -q 'stabilizationWindowSeconds: 600' on.yaml && echo "memory metric OK"
$R --set autoscaling.targetMemoryAverageValue="" > off.yaml && ! grep -q 'averageValue' off.yaml && grep -q 'averageUtilization: 70' off.yaml && echo "back-out OK"
$R --set docint.admission.enabled=false | grep -A1 'DocInt__Admission__Enabled' | grep -q 'value: "false"' && echo "false reaches the pod OK"
$R --set docint.admission.budgetBytes=0 | grep -A1 'DocInt__Admission__BudgetBytes' | grep -q 'value: "0"' && echo "zero reaches the pod OK"
$R | grep -q 'DocInt__' && echo "UNEXPECTED: defaults rendered a limit" || echo "unset limits OK"
$R > t.yaml && grep -q 'mountPath: /tmp' t.yaml && grep -q 'emptyDir' t.yaml && echo "tmp mount still OK"
$R --set otel.endpoint=http://c:4317 | grep -q 'OTEL_SERVICE_NAME' && echo "otel still OK"
```

Expected: every line prints its `OK`.

- [ ] **Step 8: Run the full gate and commit**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
git add charts/ .github/workflows/ci.yml docs/superpowers/specs/2026-07-26-eugo-docint-helm-chart-design.md
git commit -m "Scale on memory as well as CPU, now that the budget makes memory mean something"
```

---

## After the plan

The branch is ready to merge once the gate is green. Per `CLAUDE.md`, merge `feat/admission-gate` to `main` and delete the branch. `main` may still be unpushed — check `git rev-list --count origin/main..main` and confirm with the user before pushing.

**Not done by this plan, by design** (spec §11): the KEDA/collector-backed occupancy metric that should eventually replace the memory metric, tuning `targetMemoryAverageValue` against real pod metrics, a wait-duration histogram, and the `/tmp` `emptyDir` `sizeLimit`.
