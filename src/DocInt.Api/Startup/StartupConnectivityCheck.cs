using System.ClientModel;
using System.Diagnostics;
using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace DocInt.Api.Startup;

/// <summary>
/// Dials every configured Azure endpoint once, before Kestrel binds, and refuses to start the host
/// if one stays unreachable. Without it a pod comes up healthy against an endpoint it cannot
/// reach and turns every PDF or photo into a per-file engine_error — a failure that only shows up
/// in a caller's response body, never in the deployment. Failing at start puts it in the pod's
/// status instead.
/// </summary>
/// <remarks>
/// Runs in <see cref="IHostedLifecycleService.StartingAsync"/>, which the host calls for every
/// hosted service before it calls StartAsync on any of them — so this completes before
/// GenericWebHostService opens a socket, and a failure means the service never accepted a request
/// it could not fulfil. Throwing here aborts <c>app.Run()</c>, which Program's top-level handler
/// turns into a fatal log and exit code 1.
/// </remarks>
public sealed class StartupConnectivityCheck(
    IEnumerable<IStartupProbe> probes,
    IOptions<StartupProbeOptions> options,
    ILogger<StartupConnectivityCheck> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var configured = probes.ToArray();

        if (!o.Enabled)
        {
            logger.LogInformation(
                "Startup connectivity check disabled ({Key}=false); {Count} configured endpoint(s) left unverified",
                $"{StartupProbeOptions.SectionName}:Enabled", configured.Length);
            return;
        }

        if (configured.Length == 0)
        {
            logger.LogInformation(
                "Startup connectivity check: no Azure endpoint configured, nothing to verify");
            return;
        }

        logger.LogInformation(
            "Startup connectivity check: verifying {Count} endpoint(s), up to {Attempts} attempt(s) each",
            configured.Length, o.Attempts);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(o.TotalTimeoutSeconds));

        var pipeline = BuildPipeline(o);
        // Concurrently rather than in sequence: the kubelet starts counting liveness the moment
        // the container does, so the check has to cost about one endpoint's latency, not the sum.
        var outcomes = await Task.WhenAll(configured.Select(p => RunAsync(p, pipeline, o, budget.Token)));

        var failures = outcomes.OfType<string>().ToArray();   // null means the connection was established
        if (failures.Length == 0) return;

        throw new StartupProbeFailedException(
            $"Startup connectivity check failed after {o.Attempts} attempt(s): {string.Join("; ", failures)}. "
            + $"Set {StartupProbeOptions.SectionName}:Enabled=false to start without verifying.");
    }

    /// <returns><c>null</c> on success, otherwise a one-line reason naming the service.</returns>
    private async Task<string?> RunAsync(
        IStartupProbe probe, ResiliencePipeline pipeline, StartupProbeOptions o, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var attempts = 0;
        try
        {
            await pipeline.ExecuteAsync(async token =>
            {
                attempts++;
                await probe.ProbeAsync(token);
            }, ct);

            logger.LogInformation(
                "Connection to {Service} at {Endpoint} established on attempt {Attempt} of {Attempts} in {ElapsedMs} ms",
                probe.Service, probe.Endpoint, attempts, o.Attempts, ElapsedMs(started));
            return null;
        }
        catch (Exception ex)
        {
            var reason = AzureFailureDescription.Describe(ex);
            logger.LogError(
                "Connection to {Service} at {Endpoint} failed after {Attempt} attempt(s) in {ElapsedMs} ms: {Reason}",
                probe.Service, probe.Endpoint, attempts, ElapsedMs(started), reason);
            return $"{probe.Service} at {probe.Endpoint} — {reason}";
        }
    }

    private ResiliencePipeline BuildPipeline(StartupProbeOptions o) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                // Attempts counts the first try, Polly counts only the repeats.
                MaxRetryAttempts = o.Attempts - 1,
                Delay = TimeSpan.FromSeconds(o.RetryDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                // Capped because jitter can push a delay *above* its base, and the whole point of
                // TotalTimeoutSeconds is to be a budget the attempts fit inside: an uncapped delay
                // makes the worst case unknowable, and the attempt it eats is the last one — the
                // retry that exists for exactly the case where the first two failed.
                MaxDelay = TimeSpan.FromSeconds(o.RetryDelaySeconds * 2),
                ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome.Exception)),
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Startup connectivity attempt {Attempt} failed ({Reason}); retrying in {Delay}",
                        args.AttemptNumber + 1, AzureFailureDescription.Describe(args.Outcome.Exception!), args.RetryDelay);
                    return default;
                },
            })
            // Inside the retry, so the ceiling is per attempt: one hung handshake must not eat the
            // whole budget and starve the attempts that would have succeeded.
            .AddTimeout(TimeSpan.FromSeconds(o.AttemptTimeoutSeconds))
            .Build();

    /// <summary>
    /// Retry only what a second attempt could plausibly fix. A transport failure has no status —
    /// DNS not yet resolvable, TLS reset, the workload-identity token endpoint still warming up —
    /// and those are exactly the blips worth riding out. Anything with a definitive status means
    /// the service answered: a denied identity, a wrong deployment name or a
    /// publicNetworkAccess=Disabled resource will answer the same way three times in a row.
    /// </summary>
    internal static bool IsRetryable(Exception? ex) => ex is not null && StatusOf(ex) switch
    {
        null => true,
        408 or 429 => true,
        >= 500 => true,
        _ => false,
    };

    private static int? StatusOf(Exception ex) => ex switch
    {
        RequestFailedException r when r.Status > 0 => r.Status,       // Azure.Core SDKs
        ClientResultException c when c.Status > 0 => c.Status,        // System.ClientModel SDKs
        _ => null,
    };

    private static int ElapsedMs(long started) => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

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
