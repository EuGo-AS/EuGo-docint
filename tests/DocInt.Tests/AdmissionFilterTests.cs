using DocInt.Api.Admission;
using DocInt.Api.Configuration;
using DocInt.Api.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// Exercises AdmissionFilter.InvokeAsync directly, with no live host. The two HTTP-level saturation
/// tests in ExtractContractTests and TelemetryTests each get their saturation from a lease held
/// out-of-band, so neither one observes the filter's own ContentLength clamp or the filter's own
/// hold of its lease across next() — both invariants need coverage here instead.
/// </summary>
public class AdmissionFilterTests
{
    private static DocIntTelemetry TestTelemetry()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new DocIntTelemetry(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
    }

    // The filter runs before Kestrel reads a single body byte, so a request declaring far more than
    // MaxRequestBytes reaches it unfiltered. Without the Math.Min clamp, the reserved byte count
    // overshoots the limiter's own permit limit and AcquireAsync throws ArgumentOutOfRangeException
    // instead of falling through to the reader's clean 400 body_too_large — reachable from request
    // headers alone.
    [Fact]
    public async Task ContentLength_far_above_the_ceiling_is_clamped_before_reserving_budget()
    {
        using var gate = new RequestAdmissionGate(Options.Create(new AdmissionOptions
        {
            Enabled = true,
            BudgetBytes = 1_073_741_824, // shipped default: 1024 one-mebibyte permits
            QueueTimeoutSeconds = 10,
            RetryAfterSeconds = 5
        }));
        var docint = TestOptions.Wrapped(maxRequestFileBytes: 209_715_200); // MaxRequestBytes = 210_763_776
        var admission = Options.Create(new AdmissionOptions
        {
            Enabled = true,
            BudgetBytes = 1_073_741_824,
            QueueTimeoutSeconds = 10,
            RetryAfterSeconds = 5
        });
        var filter = new AdmissionFilter(gate, docint, admission, TestTelemetry());

        var http = new DefaultHttpContext();
        http.Request.ContentLength = 2_000_000_000; // far past MaxRequestBytes; nothing has read the body yet
        var context = new DefaultEndpointFilterInvocationContext(http);

        var sentinel = new object();
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(sentinel);

        var result = await filter.InvokeAsync(context, next);

        Assert.Same(sentinel, result);
    }

    // The whole point of the gate is that budget stays reserved for the life of the downstream
    // handler, not just for the moment it is acquired. Budget here is exactly one permit: the
    // outer acquire (driven by ContentLength) takes it, and next() tries to take a second one from
    // the same gate. If the filter's lease is genuinely held across next(), the inner acquire has
    // nothing left to take and is shed by the queue timeout (null). If the lease were released
    // early instead (acquire-then-dispose), the inner acquire would find the permit free again and
    // succeed (non-null) — that is the mutation this test exists to catch.
    [Fact]
    public async Task The_lease_stays_held_across_next_so_it_blocks_a_nested_acquire_on_the_same_budget()
    {
        using var gate = new RequestAdmissionGate(Options.Create(new AdmissionOptions
        {
            Enabled = true,
            BudgetBytes = 1024 * 1024, // exactly one permit
            QueueTimeoutSeconds = 1,
            RetryAfterSeconds = 5
        }));
        var docint = TestOptions.Wrapped(maxRequestFileBytes: 209_715_200);
        var admission = Options.Create(new AdmissionOptions
        {
            Enabled = true,
            BudgetBytes = 1024 * 1024,
            QueueTimeoutSeconds = 1,
            RetryAfterSeconds = 5
        });
        var filter = new AdmissionFilter(gate, docint, admission, TestTelemetry());

        var http = new DefaultHttpContext();
        http.Request.ContentLength = 1024 * 1024;
        var context = new DefaultEndpointFilterInvocationContext(http);

        var sentinel = new object();
        AdmissionLease? nested = null;
        EndpointFilterDelegate next = async _ =>
        {
            nested = await gate.AcquireAsync(1024 * 1024, CancellationToken.None);
            return sentinel;
        };

        var result = await filter.InvokeAsync(context, next);

        // Confirms next actually ran (the outer request was admitted) rather than the filter
        // shedding it before next was ever called, which would make the assertion below pass
        // for the wrong reason.
        Assert.Same(sentinel, result);
        Assert.Null(nested);
    }
}
