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

    // Null when disabled: the limiter itself is never constructed on that path. AcquireAsync's
    // disabled branch still allocates an AdmissionLease, but it wraps a null RateLimitLease and is
    // a no-op to dispose — the allocation that is avoided is the limiter and everything it queues.
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
