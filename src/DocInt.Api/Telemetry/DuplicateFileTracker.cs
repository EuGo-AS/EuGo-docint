using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Telemetry;

/// <summary>Duplicate submissions found in one batch, split by scope. See DuplicateFileTracker.</summary>
public sealed record DuplicateCounts(int WithinRequest, int AcrossRequests);

/// <summary>
/// Counts repeated file submissions from content hashes, holding a bounded FIFO of the hashes this
/// pod has seen. Returns counts and never touches the meter — the caller emits — which keeps this
/// unit testable with no host, no DI and no IMeterFactory.
/// </summary>
/// <remarks>
/// Stores 64-bit hashes only: no bytes, no filenames, nothing reconstructable. The cache is
/// per-pod and resets on restart, which is normal for counters.
/// </remarks>
public sealed class DuplicateFileTracker
{
    private readonly int _capacity;
    private readonly HashSet<ulong> _seen;
    private readonly Queue<ulong> _insertionOrder = new();
    private readonly Lock _gate = new();

    public DuplicateFileTracker(IOptions<DuplicateTrackingOptions> options)
    {
        Enabled = options.Value.Enabled;
        _capacity = options.Value.Capacity;
        // Do not pre-size to Capacity: 100 000 slots would be allocated on every pod that never
        // sees a duplicate. It grows into them.
        _seen = new HashSet<ulong>(capacity: Math.Min(_capacity, 1024));
    }

    public bool Enabled { get; }

    /// <summary>
    /// Accounts one batch. <paramref name="hashes"/> holds one entry per file that reached an
    /// engine — the caller excludes reader-rejected files, whose byte arrays are empty and would
    /// otherwise all hash alike.
    /// </summary>
    public DuplicateCounts Record(IReadOnlyList<ulong> hashes)
    {
        // Defence in depth: a disabled tracker must not grow its cache even if a caller forgets
        // to gate the call. This is only half the documented contract, though — it stops the
        // mutation, not the measurement. An unconditional caller would still get (0, 0) back and
        // record it as a real observation, creating a scope=pod series on a pod where tracking
        // is meant to be invisible. The call site still has to check Enabled before calling
        // Record at all; Task 6 does that and tests it. Do not delete either gate.
        if (!Enabled) return new DuplicateCounts(0, 0);
        if (hashes.Count == 0) return new DuplicateCounts(0, 0);

        var distinct = new HashSet<ulong>(hashes);
        // Every occurrence beyond the first in its group. Exact, and needs no retained state.
        var withinRequest = hashes.Count - distinct.Count;
        var acrossRequests = 0;

        lock (_gate)
        {
            foreach (var hash in distinct)
            {
                // Counted once per batch per distinct hash, which is what keeps this disjoint from
                // withinRequest above: neither scope can claim the same file.
                if (!_seen.Add(hash))
                {
                    acrossRequests++;
                    continue;
                }
                _insertionOrder.Enqueue(hash);
                // FIFO, not LRU: a hash keeps its original position, so a long-lived popular file
                // ages out like any other. Simpler, and the difference does not matter for a
                // diagnostic counter.
                if (_insertionOrder.Count > _capacity) _seen.Remove(_insertionOrder.Dequeue());
            }
        }

        return new DuplicateCounts(withinRequest, acrossRequests);
    }
}
