using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Health;
using DocInt.Api.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
