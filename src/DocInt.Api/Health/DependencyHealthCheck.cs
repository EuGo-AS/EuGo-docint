using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocInt.Api.Health;

/// <summary>
/// Reports one dependency's last known reachability. Reads the snapshot and returns — no I/O,
/// so /health stays well inside the kubelet's 1s probe deadline no matter how slow Azure is.
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
