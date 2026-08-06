namespace DocInt.Api.Startup;

/// <summary>
/// A one-shot reachability check for one configured Azure endpoint, run once before Kestrel binds.
/// Implementations issue the cheapest authenticated call their service offers: the point is to
/// prove DNS, TLS and credentials — not to exercise the extraction path. No document content is
/// ever sent, so a probe can never leak content into a log or into Azure.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>ILayoutAnalysisClient</c>/<c>IVisionChatClient</c> rather than a
/// method on them: reachability is not extraction, and bolting it on would make every fake in the
/// contract tests grow a member for a concern those tests do not have.
/// </remarks>
public interface IStartupProbe
{
    /// <summary>Human name for the log line, e.g. "Document Intelligence".</summary>
    string Service { get; }

    /// <summary>The endpoint being probed. Logged verbatim — a hostname, never a credential.</summary>
    string Endpoint { get; }

    Task ProbeAsync(CancellationToken ct);
}

/// <summary>
/// Thrown out of <see cref="StartupConnectivityCheck"/> when a configured endpoint stays
/// unreachable. It aborts host startup, so Program's top-level handler logs it fatal and the
/// process exits non-zero rather than serving traffic it cannot fulfil.
/// </summary>
public sealed class StartupProbeFailedException(string message) : Exception(message);
