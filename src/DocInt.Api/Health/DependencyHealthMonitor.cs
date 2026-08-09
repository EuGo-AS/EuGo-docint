using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Health;

/// <summary>
/// Re-dials every configured Azure endpoint on a timer and records the verdict, so a
/// dependency that fails *after* a successful boot becomes visible on /health instead of
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
