using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Health;
using DocInt.Api.Startup;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// The periodic dependency-reachability report behind /health. Its whole value is that a
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
            builder.UseSetting("Foundry:DocumentIntelligenceEndpoint", "https://di.example/");
            builder.UseSetting("Foundry:OpenAIEndpoint", "https://aoai.example/");
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:Enabled", enabled ? "true" : "false");
            base.ConfigureWebHost(builder);   // leaves the startup check disabled, so nothing is dialled
        }

        // This is the one factory that turns the monitor on, so it is the one that would otherwise
        // dial: the endpoints above are fake but the probes resolved against them are real, and with
        // no ApiKey they reach for DefaultAzureCredential. Dropping them keeps the suite offline and
        // costs the assertions nothing -- the checks are registered from configuration, not from the
        // probe list, and a monitor with no probes does no work.
        protected override void ConfigureFakes(IServiceCollection services) =>
            services.RemoveAll<IStartupProbe>();
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
