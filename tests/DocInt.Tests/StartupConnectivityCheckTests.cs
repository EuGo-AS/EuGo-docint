using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// The boot-time reachability check. Its whole value is that a pod with an endpoint it cannot
/// reach dies at start instead of accepting traffic and failing every PDF — so the tests that
/// matter are the two ends of that: a transient blip must not kill the host, and a permanent
/// failure must.
/// </summary>
public class StartupConnectivityCheckTests
{
    private const string Service = "Fake Service";

    private sealed class FakeStartupProbe(Func<int, Task> behaviour) : IStartupProbe
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);
        string IStartupProbe.Service => Service;
        public string Endpoint => "https://fake.example/";

        public Task ProbeAsync(CancellationToken ct) => behaviour(Interlocked.Increment(ref _attempts));

        /// <summary>Fails the first <paramref name="failures"/> attempts with a retryable 503.</summary>
        public static FakeStartupProbe FailingTimes(int failures) =>
            new(attempt => attempt <= failures
                ? Task.FromException(new RequestFailedException(503, "service unavailable"))
                : Task.CompletedTask);

        public static FakeStartupProbe Always(Exception ex) => new(_ => Task.FromException(ex));
    }

    /// <summary>Boots the real Program with the check enabled and a single fake probe in place.</summary>
    private sealed class ProbeFactory(IStartupProbe probe, bool enabled = true) : DocIntAppFactory
    {
        public CapturingLoggerProvider Capture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{StartupProbeOptions.SectionName}:Enabled", enabled ? "true" : "false");
            // Attempt spacing is what the retry contract is about, not wall-clock: 0 keeps the
            // suite fast without changing the number of attempts under test.
            builder.UseSetting($"{StartupProbeOptions.SectionName}:RetryDelaySeconds", "0");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureFakes(IServiceCollection services)
        {
            services.RemoveAll<IStartupProbe>();
            services.AddSingleton(probe);
            services.AddSingleton<ILoggerProvider>(Capture);
        }
    }

    private sealed class ConfiguredEndpointsFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Foundry:DocumentIntelligenceEndpoint", "https://di.example/");
            builder.UseSetting("Foundry:OpenAIEndpoint", "https://aoai.example/");
            base.ConfigureWebHost(builder);   // leaves the check disabled, so nothing is dialled
        }
    }

    // The stub-first contract, unchanged: a blank endpoint is a legal deployment, so there is
    // nothing to reach and the check must register no probe rather than pass vacuously.
    [Fact]
    public void No_endpoint_configured_registers_no_probe()
    {
        using var factory = new DocIntAppFactory();
        Assert.Empty(factory.Services.GetServices<IStartupProbe>());
    }

    [Fact]
    public void Each_configured_endpoint_registers_its_own_probe()
    {
        using var factory = new ConfiguredEndpointsFactory();
        Assert.Equal(2, factory.Services.GetServices<IStartupProbe>().Count());
    }

    // Three attempts, not one: a pod can start while its node's DNS or the workload-identity
    // token endpoint is still warming up, and crash-looping through that is worse than waiting.
    [Fact]
    public void Transient_failures_are_retried_and_the_host_still_starts()
    {
        var probe = FakeStartupProbe.FailingTimes(2);
        using var factory = new ProbeFactory(probe);

        _ = factory.Services;   // forces host start, which runs the check before Kestrel binds

        Assert.Equal(3, probe.Attempts);
        Assert.Contains(factory.Capture.Lines, l => l.Contains("established") && l.Contains(Service));
    }

    // The half that matters operationally: an endpoint that never answers must leave the service
    // dead rather than serving. Asserted on the host, not on the message -- WebApplicationFactory
    // tears its host down on a failed start and re-surfaces the disposal, so the exception type
    // that escapes here is the harness's, not ours. The message itself is asserted directly below.
    [Fact]
    public void Failure_on_every_attempt_stops_host_startup()
    {
        var probe = FakeStartupProbe.FailingTimes(int.MaxValue);
        using var factory = new ProbeFactory(probe);

        Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Equal(3, probe.Attempts);
    }

    [Fact]
    public void A_disabled_check_boots_without_probing()
    {
        var probe = FakeStartupProbe.Always(new RequestFailedException(403, "never reached"));
        using var factory = new ProbeFactory(probe, enabled: false);

        _ = factory.Services;

        Assert.Equal(0, probe.Attempts);
    }

    // A 403 is the service answering. Retrying a denied identity, a wrong deployment name or a
    // publicNetworkAccess=Disabled resource cannot change the answer and only burns the budget
    // the pod's probes are counting against.
    [Fact]
    public async Task A_definitive_http_status_is_not_retried()
    {
        var probe = FakeStartupProbe.Always(new RequestFailedException(403, "Public access is disabled."));

        await Assert.ThrowsAsync<StartupProbeFailedException>(() => RunDirectly(probe));

        Assert.Equal(1, probe.Attempts);
    }

    // The fatal message is the operator's only signal, so it has to carry all three things they
    // need: which endpoint, what the service said, and how to start anyway.
    [Fact]
    public async Task The_failure_names_the_service_the_status_and_the_way_out()
    {
        var probe = FakeStartupProbe.Always(new RequestFailedException(403, "Public access is disabled."));

        var ex = await Assert.ThrowsAsync<StartupProbeFailedException>(() => RunDirectly(probe));

        Assert.Contains(Service, ex.Message);
        Assert.Contains("403", ex.Message);
        Assert.Contains($"{StartupProbeOptions.SectionName}:Enabled", ex.Message);
    }

    /// <summary>
    /// Runs the check on its own, outside the host, where the exception it raises is the one the
    /// assertion sees.
    /// </summary>
    private static Task RunDirectly(IStartupProbe probe)
    {
        var options = Options.Create(new StartupProbeOptions
        {
            Enabled = true,
            Attempts = 3,
            RetryDelaySeconds = 0,
            AttemptTimeoutSeconds = 5,
            TotalTimeoutSeconds = 20,
        });
        var check = new StartupConnectivityCheck(
            [probe], options, NullLogger<StartupConnectivityCheck>.Instance);
        return check.StartingAsync(CancellationToken.None);
    }
}
