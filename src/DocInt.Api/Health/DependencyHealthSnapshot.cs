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
