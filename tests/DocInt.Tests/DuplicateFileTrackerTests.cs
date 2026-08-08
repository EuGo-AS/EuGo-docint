using DocInt.Api.Configuration;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class DuplicateFileTrackerTests
{
    private static DuplicateFileTracker Tracker(int capacity = 8) =>
        new(Options.Create(new DuplicateTrackingOptions { Capacity = capacity }));

    [Fact]
    public void Empty_batch_reports_nothing()
    {
        Assert.Equal(new DuplicateCounts(0, 0), Tracker().Record([]));
    }

    [Fact]
    public void Repeats_inside_one_batch_are_counted_once_each_beyond_the_first()
    {
        // Three copies of one hash is two duplicates, not three: the first occurrence is the
        // original. Nothing has been seen before, so the pod count stays 0.
        Assert.Equal(new DuplicateCounts(2, 0), Tracker().Record([7, 7, 7]));
    }

    [Fact]
    public void A_hash_seen_in_an_earlier_batch_counts_against_the_pod_scope()
    {
        var tracker = Tracker();
        Assert.Equal(new DuplicateCounts(0, 0), tracker.Record([1, 2]));
        Assert.Equal(new DuplicateCounts(0, 1), tracker.Record([2, 3]));
    }

    // The two scopes must never both claim the same file. A hash that is repeated within the
    // batch AND was seen before contributes exactly one to each: one repeat inside the batch,
    // one distinct hash that the pod already knew.
    [Fact]
    public void The_two_scopes_do_not_double_count()
    {
        var tracker = Tracker();
        tracker.Record([5]);
        Assert.Equal(new DuplicateCounts(1, 1), tracker.Record([5, 5]));
    }

    // FIFO eviction: with capacity 2, inserting 2 and 3 pushes 1 out, so 1 reads as new again.
    // Without this the cache would grow without bound and the pod's memory limit would decide
    // when tracking stops.
    [Fact]
    public void A_hash_evicted_by_later_inserts_is_no_longer_recognised()
    {
        var tracker = Tracker(capacity: 2);
        tracker.Record([1]);
        tracker.Record([2, 3]);
        Assert.Equal(new DuplicateCounts(0, 0), tracker.Record([1]));
    }

    [Fact]
    public void Enabled_reflects_configuration()
    {
        Assert.True(Tracker().Enabled);
        Assert.False(new DuplicateFileTracker(
            Options.Create(new DuplicateTrackingOptions { Capacity = 8, Enabled = false })).Enabled);
    }
}
