using Azure;
using DocInt.Api.Health;
using DocInt.Api.Startup;

namespace DocInt.Tests;

/// <summary>
/// The periodic dependency-reachability report behind /healthz. Its whole value is that a
/// dependency that dies *after* a successful boot becomes visible without the pod being
/// evicted — so the tests that matter are the state mapping and the fact that nothing here
/// can take the pod down.
/// </summary>
public class AzureFailureDescriptionTests
{
    [Fact]
    public void An_azure_failure_renders_as_one_short_line_status_first()
    {
        var ex = new RequestFailedException(403, "Public access is disabled.\nRequest ID: abc");

        var described = AzureFailureDescription.Describe(ex);

        Assert.StartsWith("HTTP 403", described);
        Assert.DoesNotContain("Request ID", described);
        Assert.DoesNotContain("\n", described);
    }

    [Fact]
    public void A_long_message_is_truncated()
    {
        var ex = new InvalidOperationException(new string('x', 500));

        var described = AzureFailureDescription.Describe(ex);

        Assert.True(described.Length <= 260, $"was {described.Length}");
        Assert.EndsWith("…", described);
    }

    [Fact]
    public void A_timeout_says_so_rather_than_leaking_a_type_name()
    {
        Assert.Equal("timed out", AzureFailureDescription.Describe(new OperationCanceledException()));
    }
}

public class DependencyHealthSnapshotTests
{
    [Fact]
    public void An_unprobed_dependency_reads_back_as_null()
    {
        var snapshot = new DependencyHealthSnapshot();

        Assert.Null(snapshot.Get("Azure OpenAI"));
    }

    [Fact]
    public void The_latest_write_wins()
    {
        var snapshot = new DependencyHealthSnapshot();
        var earlier = new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

        snapshot.Set("Azure OpenAI", new DependencyState(false, "HTTP 403: denied", earlier));
        snapshot.Set("Azure OpenAI", new DependencyState(true, null, earlier.AddSeconds(30)));

        var state = snapshot.Get("Azure OpenAI");
        Assert.NotNull(state);
        Assert.True(state.Reachable);
        Assert.Null(state.Reason);
        Assert.Equal(earlier.AddSeconds(30), state.CheckedAtUtc);
    }
}
