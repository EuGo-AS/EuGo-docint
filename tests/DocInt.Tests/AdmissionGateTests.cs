using DocInt.Api.Admission;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class AdmissionGateTests
{
    private static RequestAdmissionGate Gate(
        long budgetBytes = 4 * 1024 * 1024, int queueTimeoutSeconds = 10, bool enabled = true) =>
        new(Options.Create(new AdmissionOptions
        {
            Enabled = enabled,
            BudgetBytes = budgetBytes,
            QueueTimeoutSeconds = queueTimeoutSeconds,
            RetryAfterSeconds = 5
        }));

    // Permits are MiB, rounded up, floored at 1: a request must always cost something, or a
    // thousand tiny requests would each reserve nothing and the budget would bound nothing.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1024 * 1024, 1)]
    [InlineData(1024 * 1024 + 1, 2)]
    [InlineData(200L * 1024 * 1024, 200)]
    public void Permits_are_mebibytes_rounded_up(long bytes, int expected) =>
        Assert.Equal(expected, RequestAdmissionGate.Permits(bytes));

    [Fact]
    public async Task A_request_within_budget_is_admitted()
    {
        using var gate = Gate();
        using var lease = await gate.AcquireAsync(1024 * 1024, CancellationToken.None);
        Assert.NotNull(lease);
    }

    // The point of the whole component: while one request holds the budget, the next one waits and
    // then sheds rather than allocating alongside it.
    [Fact]
    public async Task A_second_request_over_budget_is_shed_after_the_queue_timeout()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 1);
        using var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);

        var shed = await gate.AcquireAsync(1024 * 1024, CancellationToken.None);

        Assert.Null(shed);
    }

    // ...and releasing the first lease lets the next one straight through, so the timeout above is
    // the budget being full and not the gate being broken.
    [Fact]
    public async Task Releasing_a_lease_frees_the_budget_for_the_next_request()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 10);
        var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);
        held.Dispose();

        using var next = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);

        Assert.NotNull(next);
    }

    // A client that hangs up while queued is not a shed request: there is nobody to answer and
    // nothing to report, so the cancellation propagates instead of becoming a 503. Same split as
    // EngineRouter makes between its per-file timeout and request abandonment.
    [Fact]
    public async Task A_client_disconnect_while_queued_propagates_rather_than_shedding()
    {
        using var gate = Gate(budgetBytes: 4 * 1024 * 1024, queueTimeoutSeconds: 30);
        using var held = await gate.AcquireAsync(4 * 1024 * 1024, CancellationToken.None);
        using var aborted = new CancellationTokenSource();

        var pending = gate.AcquireAsync(4 * 1024 * 1024, aborted.Token);
        await aborted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // Disabled means admit everything, including a request far past the budget. The request-level
    // limits in DocIntOptions are unaffected — this switch removes admission, not validation.
    [Fact]
    public async Task A_disabled_gate_admits_everything()
    {
        using var gate = Gate(budgetBytes: 1024 * 1024, enabled: false);
        using var lease = await gate.AcquireAsync(500L * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(lease);
    }
}
