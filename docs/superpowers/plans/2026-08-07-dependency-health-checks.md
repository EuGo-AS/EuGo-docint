# Azure dependency health on `/healthz` — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `/healthz` reports, per configured Azure dependency, whether this pod can currently
reach it — without ever failing the readiness probe and without doing network I/O on the
request path.

**Architecture:** A `BackgroundService` re-runs the existing `IStartupProbe` implementations
on a timer and writes an in-process snapshot. One `IHealthCheck` per dependency reads that
snapshot and returns `Healthy` or `Degraded`. A JSON `ResponseWriter` on `/healthz` renders
the report. `Degraded → 200`, so the pod is never evicted from the Service.

**Tech Stack:** .NET 10 · ASP.NET Core minimal API · `Microsoft.Extensions.Diagnostics.HealthChecks` ·
`System.Text.Json` · xUnit + `WebApplicationFactory`

**Spec:** `docs/superpowers/specs/2026-08-07-dependency-health-checks-design.md`

> **Amended 2026-08-07, after execution.** The test factories written out in Tasks 5 and 6 are
> superseded by what shipped — copy them from the tests on `main`, not from here. Registering the
> monitor unconditionally made it consume whichever fake `IStartupProbe` a test had registered for
> its own purposes, inflating `StartupConnectivityCheckTests` attempt counts (3→4, and 0→1 for the
> disabled-check test) and dialling real Azure from `ConfiguredFactory`. `DocIntAppFactory` now
> blanks `DocInt:DependencyCheck:Enabled` to `false` alongside `StartupProbe:Enabled`, so
> `ConfiguredFactory` additionally needs `RemoveAll<IStartupProbe>()` and `DegradedFactory`
> additionally needs `DependencyCheck:Enabled = "true"`. Task 3's
> `Dependency_check_defaults_bind_from_appsettings` consequently stops asserting `Enabled` through
> the factory. Only the unfiltered suite catches this class of breakage.

## Global Constraints

- `net10.0`, `Nullable` and `ImplicitUsings` enabled. No new NuGet packages in any project.
- The build gate, in this order, every time: `dotnet restore src/DocInt.slnx` →
  `dotnet build --no-restore src/DocInt.slnx` → `dotnet test --no-build src/DocInt.slnx`.
- TDD: the failing test comes first, and you run it and see it fail before writing code.
- Commit after every task. Work on the current branch `feat/dependency-health-checks`;
  never commit to `main`.
- **No document content in logs or responses.** Probes send fixed literals; nothing a caller
  supplied may reach a log line or the `/healthz` body.
- Every test runs offline. No Azure credentials, no network, no Docker.
- `/alive` must not change in any observable way: not its status code, not its body, not the
  checks it evaluates.
- Do not touch `src/ServiceDefaults/Extensions.cs` — it is stock Aspire and stays stock.
- No chart change. `charts/eugo-docint/` is not modified by any task in this plan.

## File Structure

| File | Responsibility |
| --- | --- |
| `src/DocInt.Api/Startup/AzureFailureDescription.cs` | **New.** One-line, truncated rendering of an Azure exception. Moved off `StartupConnectivityCheck`; two callers now. |
| `src/DocInt.Api/Startup/StartupConnectivityCheck.cs` | **Modify.** Loses `Describe`/`FirstLine`/`Code`; gains the health-check registration beside the probe registration. |
| `src/DocInt.Api/Startup/DocumentIntelligenceStartupProbe.cs` | **Modify.** Gains a `ServiceName` const so the name exists at registration time. |
| `src/DocInt.Api/Startup/AzureOpenAIStartupProbe.cs` | **Modify.** Same. |
| `src/DocInt.Api/Health/DependencyHealthSnapshot.cs` | **New.** The only shared mutable state: name → `DependencyState`. |
| `src/DocInt.Api/Health/DependencyHealthMonitor.cs` | **New.** `BackgroundService`: probes on a timer, writes the snapshot, logs transitions. |
| `src/DocInt.Api/Health/DependencyHealthCheck.cs` | **New.** `IHealthCheck` per dependency; reads the snapshot, no I/O. |
| `src/DocInt.Api/Health/HealthResponseWriter.cs` | **New.** The `/healthz` JSON body. |
| `src/DocInt.Api/Configuration/DocIntOptions.cs` | **Modify.** Adds `DependencyCheckOptions` + its `ValidateOnStart` rules. |
| `src/DocInt.Api/appsettings.json` | **Modify.** Ships the `DocInt:DependencyCheck` defaults. |
| `src/DocInt.Api/Program.cs:73-77` | **Modify.** `/healthz` gains explicit `ResultStatusCodes` and the JSON writer; `/alive` keeps its own separate options object. |
| `tests/DocInt.Tests/DependencyHealthTests.cs` | **New.** Unit tests for the snapshot, the check, and `ProbeOnceAsync`. |
| `tests/DocInt.Tests/HealthEndpointsTests.cs` | **Modify.** JSON assertions; the degraded-stays-200 and `/alive`-unaffected tests. |
| `tests/DocInt.Tests/OptionsTests.cs` | **Modify.** `DependencyCheck` validation. |
| `README.md` | **Modify.** Config table rows and a short section on what `/healthz` reports. |

---

### Task 1: Extract the shared Azure failure formatter

`StartupConnectivityCheck.Describe` renders an Azure exception as one truncated line, status
first. The monitor needs exactly the same rendering, so it moves to its own type rather than
being duplicated or made public on a class whose purpose is boot-time verification.

The two probes also gain a `ServiceName` const. Task 5 registers one health check per
configured endpoint and needs the dependency's name *at registration time*, before any probe
instance exists — the const keeps that name defined in exactly one place.

**Files:**
- Create: `src/DocInt.Api/Startup/AzureFailureDescription.cs`
- Modify: `src/DocInt.Api/Startup/StartupConnectivityCheck.cs:154-171`
- Modify: `src/DocInt.Api/Startup/DocumentIntelligenceStartupProbe.cs:33`
- Modify: `src/DocInt.Api/Startup/AzureOpenAIStartupProbe.cs:49`
- Modify: `src/DocInt.Api/DocInt.Api.csproj` (grant the test project access to internals)
- Test: `tests/DocInt.Tests/DependencyHealthTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `internal static string DocInt.Api.Startup.AzureFailureDescription.Describe(Exception ex)`
  - `public const string DocumentIntelligenceStartupProbe.ServiceName = "Document Intelligence"`
  - `public const string AzureOpenAIStartupProbe.ServiceName = "Azure OpenAI"`

- [ ] **Step 1: Write the failing test**

Create `tests/DocInt.Tests/DependencyHealthTests.cs`:

```csharp
using Azure;
using DocInt.Api.Startup;

namespace DocInt.Tests;

/// <summary>
/// The periodic dependency-reachability report behind /healthz. Its whole value is that a
/// dependency that dies *after* a successful boot becomes visible without the pod being
/// evicted — so the tests that matter are the state mapping and the fact that nothing here
/// can take the pod down.
/// </summary>
public class AzureFailureDescriptionTests
{
    [Fact]
    public void An_azure_failure_renders_as_one_short_line_status_first()
    {
        var ex = new RequestFailedException(403, "Public access is disabled.\nRequest ID: abc");

        var described = AzureFailureDescription.Describe(ex);

        Assert.StartsWith("HTTP 403", described);
        Assert.DoesNotContain("Request ID", described);
        Assert.DoesNotContain("\n", described);
    }

    [Fact]
    public void A_long_message_is_truncated()
    {
        var ex = new InvalidOperationException(new string('x', 500));

        var described = AzureFailureDescription.Describe(ex);

        Assert.True(described.Length <= 260, $"was {described.Length}");
        Assert.EndsWith("…", described);
    }

    [Fact]
    public void A_timeout_says_so_rather_than_leaking_a_type_name()
    {
        Assert.Equal("timed out", AzureFailureDescription.Describe(new OperationCanceledException()));
    }
}
```

`AzureFailureDescription` is `internal`, and so is the monitor's `ProbeOnceAsync` in Task 4.
Both are test seams, not API surface. **Nothing in this repo grants the test project access
to internals yet** — `StartupConnectivityCheck.IsRetryable` is `internal` but no test calls
it — so add the grant now, once, as a new `ItemGroup` in `src/DocInt.Api/DocInt.Api.csproj`:

```xml
  <ItemGroup>
    <!-- The seams stay internal rather than being widened to public: AzureFailureDescription
         and DependencyHealthMonitor.ProbeOnceAsync exist for the suite, not for callers. -->
    <InternalsVisibleTo Include="DocInt.Tests" />
  </ItemGroup>
```

`InternalsVisibleTo` as an MSBuild item is native to the SDK — it needs no package and no
hand-written `AssemblyInfo.cs`.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `AzureFailureDescription` does not exist. That is the failing
state for this task.

- [ ] **Step 3: Create the type**

Create `src/DocInt.Api/Startup/AzureFailureDescription.cs`:

```csharp
using System.ClientModel;
using Azure;
using Polly.Timeout;

namespace DocInt.Api.Startup;

/// <summary>
/// One line, status first. Azure's exception messages run to many lines and can echo the
/// request back; only the summary line belongs in a log or in a health report.
/// </summary>
/// <remarks>
/// Extracted from <see cref="StartupConnectivityCheck"/> once the periodic
/// <c>DependencyHealthMonitor</c> needed the identical rendering: the boot-time failure and
/// the running-service failure must read the same, or an operator comparing a pod log to a
/// /healthz body sees two descriptions of one fault.
/// </remarks>
internal static class AzureFailureDescription
{
    public static string Describe(Exception ex) => ex switch
    {
        RequestFailedException r => $"HTTP {r.Status}{Code(r.ErrorCode)}: {FirstLine(r.Message)}",
        ClientResultException c => $"HTTP {c.Status}: {FirstLine(c.Message)}",
        TimeoutRejectedException or OperationCanceledException => "timed out",
        _ => $"{ex.GetType().Name}: {FirstLine(ex.Message)}",
    };

    private static string Code(string? errorCode) =>
        string.IsNullOrEmpty(errorCode) ? "" : $" {errorCode}";

    private static string FirstLine(string message)
    {
        var line = message.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        return line.Length > 200 ? string.Concat(line[..200], "…") : line.ToString();
    }
}
```

- [ ] **Step 4: Delete the originals and repoint the caller**

In `src/DocInt.Api/Startup/StartupConnectivityCheck.cs`, delete the `Describe`, `Code` and
`FirstLine` methods (lines 150-171, including the doc comment above `Describe` — it moved to
the new file). Replace the three call sites with `AzureFailureDescription.Describe(...)`:

- line 92: `var reason = AzureFailureDescription.Describe(ex);`
- line 119: `AzureFailureDescription.Describe(args.Outcome.Exception!), args.RetryDelay);`

Then remove the now-unused `using System.ClientModel;` and `using Polly.Timeout;` from that
file if the compiler flags them as unused — `Azure` and `Polly.Retry` are still needed.

- [ ] **Step 5: Add the service-name constants**

In `DocumentIntelligenceStartupProbe.cs`, replace the `Service` property (line 33):

```csharp
    public const string ServiceName = "Document Intelligence";

    public string Service => ServiceName;
```

In `AzureOpenAIStartupProbe.cs`, replace the `Service` property (line 49):

```csharp
    public const string ServiceName = "Azure OpenAI";

    public string Service => ServiceName;
```

- [ ] **Step 6: Run the gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Expected: PASS, including the three new tests and the untouched
`StartupConnectivityCheckTests` — especially
`The_failure_names_the_service_the_status_and_the_way_out`, which is the regression guard
proving this refactor changed no behaviour.

- [ ] **Step 7: Commit**

```bash
git add src/DocInt.Api/Startup src/DocInt.Api/DocInt.Api.csproj tests/DocInt.Tests/DependencyHealthTests.cs
git commit -m "Refactor: share the one-line Azure failure formatter with a second caller"
```

---

### Task 2: The snapshot

The only shared mutable state in the feature: what the monitor writes and the health checks
read.

**Files:**
- Create: `src/DocInt.Api/Health/DependencyHealthSnapshot.cs`
- Test: `tests/DocInt.Tests/DependencyHealthTests.cs` (append)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public sealed record DependencyState(bool Reachable, string? Reason, DateTimeOffset CheckedAtUtc)`
  - `public sealed class DependencyHealthSnapshot` with
    `void Set(string name, DependencyState state)` and
    `DependencyState? Get(string name)` (null = never probed).

- [ ] **Step 1: Write the failing test**

Append to `tests/DocInt.Tests/DependencyHealthTests.cs`:

```csharp
public class DependencyHealthSnapshotTests
{
    [Fact]
    public void An_unprobed_dependency_reads_back_as_null()
    {
        var snapshot = new DependencyHealthSnapshot();

        Assert.Null(snapshot.Get("Azure OpenAI"));
    }

    [Fact]
    public void The_latest_write_wins()
    {
        var snapshot = new DependencyHealthSnapshot();
        var earlier = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

        snapshot.Set("Azure OpenAI", new DependencyState(false, "HTTP 403: denied", earlier));
        snapshot.Set("Azure OpenAI", new DependencyState(true, null, earlier.AddSeconds(30)));

        var state = snapshot.Get("Azure OpenAI");
        Assert.NotNull(state);
        Assert.True(state.Reachable);
        Assert.Null(state.Reason);
        Assert.Equal(earlier.AddSeconds(30), state.CheckedAtUtc);
    }
}
```

Add `using DocInt.Api.Health;` to the file's usings.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DependencyHealthSnapshot` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/DocInt.Api/Health/DependencyHealthSnapshot.cs`:

```csharp
using System.Collections.Concurrent;

namespace DocInt.Api.Health;

/// <param name="Reachable">Whether the last probe reached the service.</param>
/// <param name="Reason">One line, status first, when it did not. Null when it did.</param>
/// <param name="CheckedAtUtc">When that probe ran — the age of the verdict matters as much
/// as the verdict.</param>
public sealed record DependencyState(bool Reachable, string? Reason, DateTimeOffset CheckedAtUtc);

/// <summary>
/// The last reachability verdict per dependency, written by <see cref="DependencyHealthMonitor"/>
/// and read by <see cref="DependencyHealthCheck"/>.
/// </summary>
/// <remarks>
/// In-process and per-pod on purpose. A Workload-Identity token and a DNS resolution are
/// per-pod facts, so a shared store would have one pod reporting another's connectivity —
/// wrong for a readiness signal. It is lost on restart, which is also correct: a fresh pod
/// must not inherit a verdict it never made.
/// </remarks>
public sealed class DependencyHealthSnapshot
{
    private readonly ConcurrentDictionary<string, DependencyState> _states = new();

    public void Set(string name, DependencyState state) => _states[name] = state;

    /// <returns>The last verdict, or <c>null</c> when this dependency has never been probed.</returns>
    public DependencyState? Get(string name) => _states.TryGetValue(name, out var s) ? s : null;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~DependencyHealthSnapshotTests"
```

Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/DocInt.Api/Health/DependencyHealthSnapshot.cs tests/DocInt.Tests/DependencyHealthTests.cs
git commit -m "Add the per-pod dependency reachability snapshot"
```

---

### Task 3: Options

**Files:**
- Modify: `src/DocInt.Api/Configuration/DocIntOptions.cs`
- Modify: `src/DocInt.Api/appsettings.json`
- Test: `tests/DocInt.Tests/OptionsTests.cs` (append)

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class DependencyCheckOptions` with
  `const string SectionName = "DocInt:DependencyCheck"`, `bool Enabled` (defaults `true`),
  `int IntervalSeconds`, `int TimeoutSeconds`.

- [ ] **Step 1: Write the failing test**

Append to `tests/DocInt.Tests/OptionsTests.cs`, inside the existing class, before the closing
brace:

```csharp
    // A timeout at or above the interval lets one slow probe overlap the next tick, so the
    // pod ends up with two in-flight probes per dependency and a snapshot written out of
    // order. Rejected at boot rather than debugged in production.
    [Fact]
    public void A_timeout_that_does_not_fit_inside_the_interval_fails_host_startup()
    {
        using var factory = new OverlappingDependencyCheckFactory();
        Assert.ThrowsAny<Exception>(() => { _ = factory.Services; });
    }

    private sealed class OverlappingDependencyCheckFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:IntervalSeconds", "4");
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:TimeoutSeconds", "4");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void Dependency_check_defaults_bind_from_appsettings()
    {
        using var factory = new DocIntAppFactory();
        var o = factory.Services.GetRequiredService<IOptions<DependencyCheckOptions>>().Value;
        Assert.True(o.Enabled);
        Assert.Equal(30, o.IntervalSeconds);
        Assert.Equal(4, o.TimeoutSeconds);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DependencyCheckOptions` does not exist.

- [ ] **Step 3: Add the options class**

In `src/DocInt.Api/Configuration/DocIntOptions.cs`, after `StartupProbeOptions` (line 64):

```csharp
/// <summary>
/// The periodic reachability check that keeps /healthz honest after boot. Nested under DocInt
/// alongside StartupProbe (DocInt__DependencyCheck__Enabled from the environment).
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="StartupProbeOptions"/>. The two run at different
/// times with opposite consequences — boot-time and fatal versus periodic and informational —
/// and deriving one's default from the other's value would be exactly the hidden fallback the
/// comments in this file exist to forbid. A laptop outside the VNet turns off both, explicitly.
/// </remarks>
public sealed class DependencyCheckOptions
{
    public const string SectionName = "DocInt:DependencyCheck";

    /// <summary>
    /// The off switch, carrying its default for the same reason StartupProbe's does: a bool has
    /// no "absent", and defaulting to false would turn a typo into silent non-reporting. False
    /// registers neither the monitor nor the checks, so /healthz reports only "self" — off means
    /// silent, not pinned at "not yet checked".
    /// </summary>
    public bool Enabled { get; set; } = true;

    // No initializers below, matching the classes above: appsettings.json owns the shipped
    // values, so a section deleted by accident fails ValidateOnStart instead of falling back to
    // a second set of defaults hidden in this file.
    public int IntervalSeconds { get; set; }
    /// <summary>Ceiling on one probe. Must fit inside the interval, or ticks overlap.</summary>
    public int TimeoutSeconds { get; set; }
}
```

- [ ] **Step 4: Register and validate it**

In `OptionsExtensions.AddDocIntOptions`, immediately after the `StartupProbeOptions` block
(line 80):

```csharp
        builder.Services.AddOptions<DependencyCheckOptions>()
            .Bind(builder.Configuration.GetSection(DependencyCheckOptions.SectionName))
            .Validate(o => o.IntervalSeconds > 0 && o.TimeoutSeconds > 0,
                $"{DependencyCheckOptions.SectionName} interval and timeout must be positive")
            .Validate(o => o.TimeoutSeconds < o.IntervalSeconds,
                $"{DependencyCheckOptions.SectionName}:TimeoutSeconds must be less than "
                + "IntervalSeconds, or a slow probe overlaps the next tick")
            .ValidateOnStart();
```

- [ ] **Step 5: Ship the defaults**

In `src/DocInt.Api/appsettings.json`, inside the `"DocInt"` object, after the `StartupProbe`
block (line 27):

```jsonc
    // The running-service counterpart to StartupProbe above: it re-dials the same endpoints
    // every IntervalSeconds and reports the result on /healthz. A failure is never fatal and
    // never evicts the pod — /healthz answers 200 with a Degraded body — because the endpoints
    // are shared by every replica, so failing readiness would empty the Service instead of
    // shedding load, and would take the Azure-free XLSX path down with it.
    // TimeoutSeconds must stay below IntervalSeconds (enforced at boot) so a slow probe cannot
    // overlap the next tick. At 30s this costs 2 calls/min per dependency per pod; for Azure
    // OpenAI that is a one-token completion against the same quota real vision traffic uses.
    "DependencyCheck": {
      "Enabled": true,
      "IntervalSeconds": 30,
      "TimeoutSeconds": 4
    }
```

Remember the comma after the `StartupProbe` block's closing brace.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~OptionsTests"
```

Expected: PASS, including the two new tests.

- [ ] **Step 7: Commit**

```bash
git add src/DocInt.Api/Configuration/DocIntOptions.cs src/DocInt.Api/appsettings.json tests/DocInt.Tests/OptionsTests.cs
git commit -m "Add DocInt:DependencyCheck options with an interval-fits-timeout rule"
```

---

### Task 4: The monitor

The one component that touches the network. Everything about it is shaped by a single
requirement: **it must never be able to take the pod down.**

**Files:**
- Create: `src/DocInt.Api/Health/DependencyHealthMonitor.cs`
- Test: `tests/DocInt.Tests/DependencyHealthTests.cs` (append)

**Interfaces:**
- Consumes: `DependencyHealthSnapshot`, `DependencyState` (Task 2); `DependencyCheckOptions`
  (Task 3); `AzureFailureDescription.Describe` (Task 1); the existing
  `DocInt.Api.Startup.IStartupProbe`.
- Produces: `public sealed class DependencyHealthMonitor : BackgroundService` with a
  constructor taking `(IEnumerable<IStartupProbe> probes, IOptions<DependencyCheckOptions>
  options, DependencyHealthSnapshot snapshot, ILogger<DependencyHealthMonitor> logger)` and
  the test seam `internal Task ProbeOnceAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/DocInt.Tests/DependencyHealthTests.cs`:

```csharp
public class DependencyHealthMonitorTests
{
    private const string Service = "Fake Service";

    private sealed class FakeProbe(Func<CancellationToken, Task> behaviour) : IStartupProbe
    {
        public string Service => DependencyHealthMonitorTests.Service;
        public string Endpoint => "https://fake.example/";
        public Task ProbeAsync(CancellationToken ct) => behaviour(ct);

        public static FakeProbe Succeeding() => new(_ => Task.CompletedTask);
        public static FakeProbe Failing(Exception ex) => new(_ => Task.FromException(ex));
        public static FakeProbe Hanging() => new(ct => Task.Delay(Timeout.Infinite, ct));
    }

    private static DependencyHealthMonitor Monitor(
        IStartupProbe[] probes, DependencyHealthSnapshot snapshot, ILogger<DependencyHealthMonitor>? logger = null) =>
        new(probes,
            Options.Create(new DependencyCheckOptions { Enabled = true, IntervalSeconds = 30, TimeoutSeconds = 1 }),
            snapshot,
            logger ?? NullLogger<DependencyHealthMonitor>.Instance);

    [Fact]
    public async Task A_reachable_dependency_is_recorded_reachable_with_no_reason()
    {
        var snapshot = new DependencyHealthSnapshot();

        await Monitor([FakeProbe.Succeeding()], snapshot).ProbeOnceAsync(CancellationToken.None);

        var state = snapshot.Get(Service);
        Assert.NotNull(state);
        Assert.True(state.Reachable);
        Assert.Null(state.Reason);
    }

    [Fact]
    public async Task A_failure_is_recorded_as_one_line_naming_the_status()
    {
        var snapshot = new DependencyHealthSnapshot();
        var probe = FakeProbe.Failing(new RequestFailedException(403, "Public access is disabled."));

        await Monitor([probe], snapshot).ProbeOnceAsync(CancellationToken.None);

        var state = snapshot.Get(Service);
        Assert.NotNull(state);
        Assert.False(state.Reachable);
        Assert.Contains("403", state.Reason);
    }

    // The timeout is what keeps one hung handshake from overlapping the next tick.
    [Fact]
    public async Task A_hanging_probe_times_out_and_is_recorded_unreachable()
    {
        var snapshot = new DependencyHealthSnapshot();

        await Monitor([FakeProbe.Hanging()], snapshot).ProbeOnceAsync(CancellationToken.None);

        var state = snapshot.Get(Service);
        Assert.NotNull(state);
        Assert.False(state.Reachable);
        Assert.Equal("timed out", state.Reason);
    }

    // 2,880 identical lines per pod per day is not a signal. Only the edges are.
    [Fact]
    public async Task Only_the_transition_is_logged_not_every_tick()
    {
        var snapshot = new DependencyHealthSnapshot();
        var capture = new CapturingLoggerProvider();
        var monitor = Monitor(
            [FakeProbe.Failing(new RequestFailedException(403, "denied"))],
            snapshot,
            new LoggerFactory([capture]).CreateLogger<DependencyHealthMonitor>());

        await monitor.ProbeOnceAsync(CancellationToken.None);
        await monitor.ProbeOnceAsync(CancellationToken.None);

        Assert.Single(capture.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Service));
    }

    [Fact]
    public async Task A_recovery_is_logged_once_at_information()
    {
        var snapshot = new DependencyHealthSnapshot();
        var capture = new CapturingLoggerProvider();
        var failing = true;
        var probe = new FakeProbe(_ => failing
            ? Task.FromException(new RequestFailedException(503, "unavailable"))
            : Task.CompletedTask);
        var monitor = Monitor([probe], snapshot, new LoggerFactory([capture]).CreateLogger<DependencyHealthMonitor>());

        await monitor.ProbeOnceAsync(CancellationToken.None);
        failing = false;
        await monitor.ProbeOnceAsync(CancellationToken.None);
        await monitor.ProbeOnceAsync(CancellationToken.None);

        Assert.Single(capture.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(Service));
    }

    // The stub-first deployment: nothing configured, nothing to probe, nothing to report.
    [Fact]
    public async Task No_probes_means_no_work_and_an_empty_snapshot()
    {
        var snapshot = new DependencyHealthSnapshot();

        await Monitor([], snapshot).ProbeOnceAsync(CancellationToken.None);

        Assert.Null(snapshot.Get(Service));
    }
}
```

Add to the file's usings: `using Microsoft.Extensions.Logging;`,
`using Microsoft.Extensions.Logging.Abstractions;`, `using Microsoft.Extensions.Options;`.
`CapturingLoggerProvider` already exists in `tests/DocInt.Tests/RedactionTests.cs:8` and
exposes `Entries` as `(LogLevel Level, string Message)` — do not create a second one.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DependencyHealthMonitor` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/DocInt.Api/Health/DependencyHealthMonitor.cs`:

```csharp
using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Health;

/// <summary>
/// Re-dials every configured Azure endpoint on a timer and records the verdict, so a
/// dependency that fails *after* a successful boot becomes visible on /healthz instead of
/// showing up only as an engine_error in a caller's response body.
/// </summary>
/// <remarks>
/// The timer, rather than dialling inside the request: the pod's readinessProbe sets no
/// timeoutSeconds, so the kubelet's 1s default applies, and a handler doing a 4s round trip
/// would fail the probe on timeout — evicting the pod, which is precisely what reporting
/// instead of failing exists to avoid.
/// <para>
/// Separate from <see cref="StartupConnectivityCheck"/> because the two have opposite failure
/// semantics: that one is fatal and Polly-retried, this one is informational and never
/// retried — the next tick is the retry.
/// </para>
/// </remarks>
public sealed class DependencyHealthMonitor(
    IEnumerable<IStartupProbe> probes,
    IOptions<DependencyCheckOptions> options,
    DependencyHealthSnapshot snapshot,
    ILogger<DependencyHealthMonitor> logger) : BackgroundService
{
    private readonly IStartupProbe[] _probes = probes.ToArray();
    private readonly Dictionary<string, bool> _previous = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_probes.Length == 0) return;

        var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        logger.LogInformation(
            "Dependency health monitor watching {Count} endpoint(s) every {Interval}",
            _probes.Length, interval);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            // Nothing may escape this loop. An exception out of ExecuteAsync stops the host
            // under BackgroundServiceExceptionBehavior.StopHost, so a monitor that reports an
            // outage would instead cause one.
            try
            {
                await ProbeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dependency health tick failed unexpectedly; continuing");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One round over every probe. The test seam: the loop above is a timer, this is the
    /// behaviour.
    /// </summary>
    internal async Task ProbeOnceAsync(CancellationToken ct)
    {
        // Concurrently, then applied in sequence: the probes are independent, but _previous is
        // not thread-safe and transition logging must see one write at a time.
        var results = await Task.WhenAll(_probes.Select(p => ProbeAsync(p, ct)));
        foreach (var (probe, state) in results) Apply(probe, state);
    }

    private async Task<(IStartupProbe Probe, DependencyState State)> ProbeAsync(
        IStartupProbe probe, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            await probe.ProbeAsync(attempt.Token);
            return (probe, new DependencyState(true, null, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutdown, not a fault: let ExecuteAsync exit without recording a verdict
        }
        catch (Exception ex)
        {
            return (probe, new DependencyState(false, AzureFailureDescription.Describe(ex), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Records the verdict and logs only the edges — a steady state is not news.</summary>
    private void Apply(IStartupProbe probe, DependencyState state)
    {
        snapshot.Set(probe.Service, state);

        var changed = !_previous.TryGetValue(probe.Service, out var was) || was != state.Reachable;
        _previous[probe.Service] = state.Reachable;
        if (!changed) return;

        if (state.Reachable)
        {
            logger.LogInformation(
                "{Service} at {Endpoint} is reachable again", probe.Service, probe.Endpoint);
        }
        else
        {
            logger.LogWarning(
                "{Service} at {Endpoint} is unreachable: {Reason}",
                probe.Service, probe.Endpoint, state.Reason);
        }
    }
}
```

Note on `A_recovery_is_logged_once_at_information`: the first tick fails (one `Warning`), the
second recovers (one `Information`), the third stays reachable (nothing). The
`logger.LogInformation` in `ExecuteAsync` is not reached, because the test calls
`ProbeOnceAsync` directly — that is why `Assert.Single` on `Information` holds.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~DependencyHealthMonitorTests"
```

Expected: PASS, 6 tests. The hanging-probe test should finish in about one second — if it
takes longer, `TimeoutSeconds` is not being applied.

- [ ] **Step 5: Commit**

```bash
git add src/DocInt.Api/Health/DependencyHealthMonitor.cs tests/DocInt.Tests/DependencyHealthTests.cs
git commit -m "Add the periodic dependency reachability monitor"
```

---

### Task 5: The health check and its registration

**Files:**
- Create: `src/DocInt.Api/Health/DependencyHealthCheck.cs`
- Modify: `src/DocInt.Api/Startup/StartupConnectivityCheck.cs:182-202` (the extensions class)
- Test: `tests/DocInt.Tests/DependencyHealthTests.cs` (append)

**Interfaces:**
- Consumes: `DependencyHealthSnapshot`, `DependencyState` (Task 2); `DependencyCheckOptions`
  (Task 3); `DependencyHealthMonitor` (Task 4); the `ServiceName` consts (Task 1).
- Produces: `public sealed class DependencyHealthCheck : IHealthCheck`, and registration of
  one such check per configured endpoint plus the monitor, from
  `AddStartupConnectivityCheck`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/DocInt.Tests/DependencyHealthTests.cs`:

```csharp
public class DependencyHealthCheckTests
{
    private const string Service = "Fake Service";

    private static async Task<HealthCheckResult> Run(DependencyHealthSnapshot snapshot) =>
        await new DependencyHealthCheck(Service, "https://fake.example/", snapshot)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    [Fact]
    public async Task A_reachable_dependency_is_healthy_and_carries_its_endpoint()
    {
        var snapshot = new DependencyHealthSnapshot();
        snapshot.Set(Service, new DependencyState(true, null, DateTimeOffset.UtcNow));

        var result = await Run(snapshot);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("https://fake.example/", result.Data["endpoint"]);
    }

    // Degraded, never Unhealthy: the endpoints are shared by every replica, so an outage that
    // returned 503 here would empty the Service rather than shed load.
    [Fact]
    public async Task An_unreachable_dependency_is_degraded_and_carries_the_reason()
    {
        var snapshot = new DependencyHealthSnapshot();
        snapshot.Set(Service, new DependencyState(false, "HTTP 403: denied", DateTimeOffset.UtcNow));

        var result = await Run(snapshot);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("HTTP 403: denied", result.Description);
    }

    // Not seeded from the startup check: that check may have been disabled, and inheriting a
    // verdict it never made would be a lie.
    [Fact]
    public async Task A_dependency_probed_for_the_first_time_says_so()
    {
        var result = await Run(new DependencyHealthSnapshot());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("not yet checked", result.Description);
    }
}

public class DependencyHealthRegistrationTests
{
    private sealed class ConfiguredFactory(bool enabled = true) : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DocumentIntelligence:Endpoint", "https://di.example/");
            builder.UseSetting("AzureOpenAI:Endpoint", "https://aoai.example/");
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:Enabled", enabled ? "true" : "false");
            base.ConfigureWebHost(builder);   // leaves the startup check disabled, so nothing is dialled
        }
    }

    private static string[] CheckNames(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Select(r => r.Name).ToArray();

    // One configured endpoint, one probe, one check — registered in one place so they cannot drift.
    [Fact]
    public void Each_configured_endpoint_registers_its_own_check_alongside_self()
    {
        using var factory = new ConfiguredFactory();

        var names = CheckNames(factory);

        Assert.Contains("self", names);
        Assert.Contains(DocumentIntelligenceStartupProbe.ServiceName, names);
        Assert.Contains(AzureOpenAIStartupProbe.ServiceName, names);
    }

    [Fact]
    public void No_endpoint_configured_leaves_only_self()
    {
        using var factory = new DocIntAppFactory();

        Assert.Equal(["self"], CheckNames(factory));
    }

    // Off means silent, not stuck: registering the checks without the monitor that feeds them
    // would pin every dependency at "not yet checked" forever.
    [Fact]
    public void A_disabled_dependency_check_registers_neither_the_checks_nor_the_monitor()
    {
        using var factory = new ConfiguredFactory(enabled: false);

        Assert.Equal(["self"], CheckNames(factory));
        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<DependencyHealthMonitor>());
    }
}
```

Add to the file's usings: `using Microsoft.AspNetCore.Mvc.Testing;`,
`using Microsoft.Extensions.DependencyInjection;`,
`using Microsoft.Extensions.Diagnostics.HealthChecks;`,
`using Microsoft.Extensions.Hosting;`.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build --no-restore src/DocInt.slnx
```

Expected: **compile error** — `DependencyHealthCheck` does not exist.

- [ ] **Step 3: Write the health check**

Create `src/DocInt.Api/Health/DependencyHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocInt.Api.Health;

/// <summary>
/// Reports one dependency's last known reachability. Reads the snapshot and returns — no I/O,
/// so /healthz stays well inside the kubelet's 1s probe deadline no matter how slow Azure is.
/// </summary>
/// <remarks>
/// A failure is <see cref="HealthStatus.Degraded"/>, never Unhealthy, and the endpoint maps
/// Degraded to 200: the dependency is shared by every replica, so failing readiness would
/// empty the Service rather than shed load, and would take the Azure-free XLSX path down with
/// it. Registered without the "live" tag, so /alive never evaluates it.
/// </remarks>
public sealed class DependencyHealthCheck(string service, string endpoint, DependencyHealthSnapshot snapshot)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = snapshot.Get(service);

        // Never seeded from the startup check: it may have been disabled, and inheriting a
        // verdict it never made would be a lie. The window is one probe after boot.
        if (state is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "not yet checked", data: new Dictionary<string, object> { ["endpoint"] = endpoint }));
        }

        var data = new Dictionary<string, object>
        {
            ["endpoint"] = endpoint,
            ["lastCheckedUtc"] = state.CheckedAtUtc,
        };

        return Task.FromResult(state.Reachable
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Degraded(state.Reason, data: data));
    }
}
```

- [ ] **Step 4: Register the checks and the monitor**

In `src/DocInt.Api/Startup/StartupConnectivityCheck.cs`, replace the whole
`StartupConnectivityCheckExtensions` class (lines 182-202) with:

```csharp
public static class StartupConnectivityCheckExtensions
{
    /// <summary>
    /// Registers a probe per configured endpoint, and nothing at all when both are blank — the
    /// stub-first deployment stays legal, and only an endpoint someone actually asked for is
    /// treated as one the service must be able to reach.
    /// </summary>
    /// <remarks>
    /// The periodic health checks are registered here too, from the same condition, so the two
    /// lists cannot drift: one configured endpoint, one probe, one check.
    /// </remarks>
    public static WebApplicationBuilder AddStartupConnectivityCheck(this WebApplicationBuilder builder)
    {
        var dependencyChecks = DependencyCheckEnabled(builder);

        if (IsSet(builder, $"{DocumentIntelligenceOptions.SectionName}:Endpoint"))
        {
            builder.Services.AddSingleton<IStartupProbe, DocumentIntelligenceStartupProbe>();
            if (dependencyChecks)
            {
                AddDependencyCheck(builder, DocumentIntelligenceStartupProbe.ServiceName,
                    builder.Configuration[$"{DocumentIntelligenceOptions.SectionName}:Endpoint"]!);
            }
        }

        if (IsSet(builder, $"{AzureOpenAIOptions.SectionName}:Endpoint"))
        {
            builder.Services.AddSingleton<IStartupProbe, AzureOpenAIStartupProbe>();
            if (dependencyChecks)
            {
                AddDependencyCheck(builder, AzureOpenAIStartupProbe.ServiceName,
                    builder.Configuration[$"{AzureOpenAIOptions.SectionName}:Endpoint"]!);
            }
        }

        builder.Services.AddHostedService<StartupConnectivityCheck>();

        if (dependencyChecks)
        {
            builder.Services.AddSingleton<DependencyHealthSnapshot>();
            builder.Services.AddHostedService<DependencyHealthMonitor>();
        }

        return builder;
    }

    /// <summary>
    /// No "live" tag, deliberately: /alive filters on it, and a dependency outage must never
    /// restart a pod that is serving correctly.
    /// </summary>
    private static void AddDependencyCheck(WebApplicationBuilder builder, string service, string endpoint) =>
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            service,
            sp => new DependencyHealthCheck(service, endpoint, sp.GetRequiredService<DependencyHealthSnapshot>()),
            failureStatus: HealthStatus.Degraded,
            tags: null));

    /// <summary>
    /// Read straight from configuration: options are not bound yet at registration time. Absent
    /// or unparseable means on, matching <see cref="DependencyCheckOptions.Enabled"/>'s default.
    /// </summary>
    private static bool DependencyCheckEnabled(WebApplicationBuilder builder) =>
        !bool.TryParse(builder.Configuration[$"{DependencyCheckOptions.SectionName}:Enabled"], out var enabled)
        || enabled;

    private static bool IsSet(WebApplicationBuilder builder, string key) =>
        !string.IsNullOrWhiteSpace(builder.Configuration[key]);
}
```

Add to that file's usings: `using DocInt.Api.Health;` and
`using Microsoft.Extensions.Diagnostics.HealthChecks;`.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~DependencyHealth"
```

Expected: PASS — the check tests and the three registration tests.

- [ ] **Step 6: Run the whole gate**

```bash
dotnet test --no-build src/DocInt.slnx
```

Expected: PASS. `StartupConnectivityCheckTests.Each_configured_endpoint_registers_its_own_probe`
in particular must still pass — the probe registration is unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/DocInt.Api/Health/DependencyHealthCheck.cs src/DocInt.Api/Startup/StartupConnectivityCheck.cs tests/DocInt.Tests/DependencyHealthTests.cs
git commit -m "Register a health check per configured Azure endpoint"
```

---

### Task 6: The `/healthz` body

The user-visible half, and the two tests the whole design rests on.

**Files:**
- Create: `src/DocInt.Api/Health/HealthResponseWriter.cs`
- Modify: `src/DocInt.Api/Program.cs:73-77`
- Test: `tests/DocInt.Tests/HealthEndpointsTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: `internal static Task HealthResponseWriter.WriteAsync(HttpContext context, HealthReport report)`.

- [ ] **Step 1: Write the failing tests**

In `tests/DocInt.Tests/HealthEndpointsTests.cs`, replace `Healthz_returns_healthy`
(lines 12-18) with the JSON form, and add the two new tests plus their factory. The full new
file content:

```csharp
using System.Net;
using System.Text.Json;
using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocInt.Tests;

public class HealthEndpointsTests : IClassFixture<DocIntAppFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(DocIntAppFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Healthz_returns_healthy()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
        var names = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["self"], names);   // no endpoint configured, so no dependency checks
    }

    [Fact]
    public async Task Alive_returns_healthy()
    {
        var response = await _client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Info_returns_service_metadata()
    {
        var response = await _client.GetAsync("/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EuGo-docint", doc.RootElement.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        var endpoints = doc.RootElement.GetProperty("endpoints").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("/healthz", endpoints);
        Assert.Contains("/alive", endpoints);
        Assert.Contains("/info", endpoints);
    }
}

/// <summary>
/// A dependency that is down while the pod keeps serving. Reachable only with the startup
/// check disabled — with it on, an unreachable endpoint aborts the boot and there is no
/// running pod to ask.
/// </summary>
public class DegradedDependencyTests
{
    // The fake stands in for the Document Intelligence probe, so it must answer to the same
    // name: the monitor writes the snapshot under probe.Service, and the check registered for
    // a configured DI endpoint reads it under DocumentIntelligenceStartupProbe.ServiceName.
    // Any other name here leaves the check pinned at "not yet checked" and the test green for
    // the wrong reason.
    private static readonly string Service = DocumentIntelligenceStartupProbe.ServiceName;
    private const string Endpoint = "https://di.example/";

    private sealed class UnreachableProbe : IStartupProbe
    {
        public string Service => DocumentIntelligenceStartupProbe.ServiceName;
        public string Endpoint => DegradedDependencyTests.Endpoint;
        public Task ProbeAsync(CancellationToken ct) =>
            Task.FromException(new RequestFailedException(403, "Public access is disabled."));
    }

    private sealed class DegradedFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocumentIntelligence:Endpoint", Endpoint);
            builder.UseSetting($"{StartupProbeOptions.SectionName}:Enabled", "false");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureFakes(IServiceCollection services)
        {
            services.RemoveAll<IStartupProbe>();
            services.AddSingleton<IStartupProbe, UnreachableProbe>();
        }
    }

    /// <summary>
    /// Polls until the background monitor has written its first verdict. The monitor probes
    /// immediately on start, so this settles in milliseconds; the loop exists so the test does
    /// not race the host.
    /// </summary>
    private static async Task<JsonDocument> DegradedBody(HttpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            var response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = doc.RootElement.GetProperty("status").GetString();
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("name").GetString() == Service);
            if (status == "Degraded" && check.ValueKind is JsonValueKind.Object
                && check.TryGetProperty("reason", out var reason) && reason.GetString()!.Contains("403"))
            {
                return doc;
            }
            doc.Dispose();
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("the monitor never reported the dependency unreachable");
    }

    // THE load-bearing test. A dependency outage must not evict the pod: the endpoints are
    // shared by every replica, so a 503 here empties the Service instead of shedding load, and
    // takes the Azure-free XLSX path down with it. Fails the moment someone remaps Degraded.
    [Fact]
    public async Task An_unreachable_dependency_is_reported_but_healthz_still_answers_200()
    {
        using var factory = new DegradedFactory();
        using var doc = await DegradedBody(factory.CreateClient());

        var check = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == Service);
        Assert.Equal("Degraded", check.GetProperty("status").GetString());
        // From configuration, not from the probe: the check is constructed at registration
        // time with the endpoint the operator configured.
        Assert.Equal(Endpoint, check.GetProperty("endpoint").GetString());
        Assert.Contains("403", check.GetProperty("reason").GetString());
        Assert.True(check.TryGetProperty("lastCheckedUtc", out _));
    }

    // Liveness must not move: restarting a pod that is serving correctly fixes nothing and
    // costs a rolling outage. Guards both the missing "live" tag and the separate options object.
    [Fact]
    public async Task Alive_is_unaffected_by_a_degraded_dependency()
    {
        using var factory = new DegradedFactory();
        var client = factory.CreateClient();
        (await DegradedBody(client)).Dispose();

        var response = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~HealthEndpointsTests|FullyQualifiedName~DegradedDependencyTests"
```

Expected: FAIL — `Healthz_returns_healthy` fails parsing the plain-text body `Healthy` as
JSON, and the two new tests time out in `DegradedBody`.

- [ ] **Step 3: Write the response writer**

Create `src/DocInt.Api/Health/HealthResponseWriter.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocInt.Api.Health;

/// <summary>
/// Renders the health report as JSON, so /healthz can say *which* dependency is unreachable
/// and why — the plain-text default carries only an aggregate word.
/// </summary>
/// <remarks>
/// The body names Azure hostnames and a one-line failure reason on an unauthenticated route.
/// That is acceptable here and only here: the service is cluster-internal with no ingress, and
/// the startup logs already record the same hostnames verbatim. No caller-supplied content can
/// reach it — probes send fixed literals.
/// </remarks>
internal static class HealthResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());
            json.WriteStartArray("checks");
            foreach (var (name, entry) in report.Entries)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteString("status", entry.Status.ToString());
                if (entry.Data.TryGetValue("endpoint", out var endpoint))
                {
                    json.WriteString("endpoint", endpoint.ToString());
                }
                if (entry.Data.TryGetValue("lastCheckedUtc", out var checkedAt)
                    && checkedAt is DateTimeOffset at)
                {
                    json.WriteString("lastCheckedUtc", at.ToUniversalTime().ToString("O"));
                }
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    json.WriteString("reason", entry.Description);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray());
    }
}
```

- [ ] **Step 4: Wire it into `/healthz`**

In `src/DocInt.Api/Program.cs`, replace lines 73-77 with:

```csharp
    // Two separate options objects, deliberately. /healthz is readiness and carries the
    // dependency report; /alive is liveness and must stay a plain-text, local-only answer —
    // sharing one object here would silently change the liveness body.
    app.MapHealthChecks("/healthz", new HealthCheckOptions
    {
        ResponseWriter = HealthResponseWriter.WriteAsync,
        // Explicit, though these are the framework's defaults: the whole design rests on a
        // failing dependency not evicting the pod, and that must not be an inherited default
        // someone can change without failing a test.
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
    });
    app.MapHealthChecks("/alive", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("live")
    });
```

Add `using DocInt.Api.Health;` and `using Microsoft.Extensions.Diagnostics.HealthChecks;` to
`Program.cs` — `Microsoft.AspNetCore.Diagnostics.HealthChecks` (line 7) is already there and
is still needed for `HealthCheckOptions`.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~HealthEndpointsTests|FullyQualifiedName~DegradedDependencyTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 6: Run the whole gate**

```bash
dotnet restore src/DocInt.slnx
dotnet build --no-restore src/DocInt.slnx
dotnet test --no-build src/DocInt.slnx
```

Expected: PASS, whole suite. If `LiveSmokeTests` report as skipped, that is correct —
`DOCINT_LIVE_TESTS` is not set.

- [ ] **Step 7: Commit**

```bash
git add src/DocInt.Api/Health/HealthResponseWriter.cs src/DocInt.Api/Program.cs tests/DocInt.Tests/HealthEndpointsTests.cs
git commit -m "Report per-dependency reachability in the /healthz body, still 200"
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md:149-160` (the settings excerpt), `README.md:187-195` (the settings
  table), and the health-endpoint prose

**Interfaces:**
- Consumes: the config keys from Task 3 and the body shape from Task 6.
- Produces: nothing code-facing.

- [ ] **Step 1: Update the `appsettings.json` excerpt**

In `README.md`, in the `jsonc` block starting at line 149, add the `DependencyCheck` block
after `StartupProbe`'s closing brace, matching what Task 3 put in `appsettings.json` but with
the shorter comment:

```jsonc
    // Periodic reachability check over the same endpoints; reported on /healthz, never fatal.
    "DependencyCheck": {
      "Enabled": true,
      "IntervalSeconds": 30,
      "TimeoutSeconds": 4
    }
```

- [ ] **Step 2: Add the settings-table rows**

In the `### Application settings` table, after the
`DocInt:StartupProbe:TotalTimeoutSeconds` row (line 195):

```markdown
| `DocInt:DependencyCheck:Enabled` | `true` | `extraEnv` | Re-dial every configured endpoint every `IntervalSeconds` and report it on `/healthz`. False registers neither the monitor nor the checks, so `/healthz` reports only `self` |
| `DocInt:DependencyCheck:IntervalSeconds` | `30` | `extraEnv` | Seconds between rounds. At 30 s this is 2 calls/min per dependency per pod; for Azure OpenAI that is a one-token completion against the same quota real vision traffic uses |
| `DocInt:DependencyCheck:TimeoutSeconds` | `4` | `extraEnv` | Ceiling on one probe. Must be **less than** `IntervalSeconds` (rejected at boot otherwise), so a slow probe cannot overlap the next tick |
```

- [ ] **Step 3: Correct the two stale StartupProbe defaults**

While in this table: `DocInt:StartupProbe:RetryDelaySeconds` reads `1` and
`DocInt:StartupProbe:TotalTimeoutSeconds` reads `18`, but `appsettings.json` has carried `2`
and `25` since commits `b97f875` and `b64e08d`. Correct both cells to the shipped values, and
the `RetryDelaySeconds` line in the excerpt at line 159 from `1` to `2`.

- [ ] **Step 4: Document what `/healthz` reports**

Add a subsection immediately after the `## Startup connectivity check` section (which ends at
line 257), before whatever follows it:

```markdown
## What `/healthz` reports

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
deployment shows just `self`.

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
successful start — which is exactly the gap this closes.
```

- [ ] **Step 5: Verify the docs match the code**

```bash
dotnet run --project src/DocInt.Api
```

In another shell: `curl -s localhost:8090/healthz` and confirm the body matches the documented
shape (with no endpoints configured it will show only `self`). Stop the app.

If `src/DocInt.Api/appsettings.Development.json` exists on your machine and carries real
endpoints, the app will try to dial them at boot; run with
`DocInt__StartupProbe__Enabled=false` if you are outside the VNet.

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "Docs: describe the /healthz dependency report and its config"
```

---

## Done criteria

- `dotnet restore src/DocInt.slnx` → `dotnet build --no-restore src/DocInt.slnx` →
  `dotnet test --no-build src/DocInt.slnx` all green, in that order.
- `/healthz` returns 200 with a JSON body naming every configured dependency.
- `/alive` returns 200 and exactly `Healthy`, unchanged, with a dependency down.
- No chart file modified; no new NuGet package; `ServiceDefaults/Extensions.cs` untouched.
