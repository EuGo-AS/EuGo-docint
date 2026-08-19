using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// The retired per-surface configuration keys, and why leaving one behind has to be fatal.
/// </summary>
/// <remarks>
/// A Foundry account has one key pair covering every API it exposes, so DocumentIntelligence:ApiKey
/// and AzureOpenAI:ApiKey were two names for one secret. Simply dropping the old names would make a
/// stale value silently unbound — and an absent key is a legal configuration meaning "use
/// DefaultAzureCredential", so the service would come up on an authentication leg nobody chose and
/// report it later as an endpoint or network failure. Failing at boot, naming the replacement, is
/// the difference between a five-second fix and an afternoon spent on DNS.
/// </remarks>
public class RetiredConfigurationKeyTests
{
    public static TheoryData<string, string> RetiredKeys => new()
    {
        { "DocumentIntelligence:Endpoint", "Foundry:DocumentIntelligenceEndpoint" },
        { "DocumentIntelligence:ApiKey", "Foundry:ApiKey" },
        { "AzureOpenAI:Endpoint", "Foundry:OpenAIEndpoint" },
        { "AzureOpenAI:ApiKey", "Foundry:ApiKey" },
        { "AzureOpenAI:DeploymentNameVision", "Foundry:DeploymentNameVision" },
    };

    [Theory]
    [MemberData(nameof(RetiredKeys))]
    public void A_retired_key_fails_startup_and_names_its_replacement(string retired, string replacement)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate((retired, "something")));
        Assert.Contains(retired, ex.Message);
        Assert.Contains(replacement, ex.Message);
    }

    // The shape a real migration leaves behind: the value survives in the environment (or in
    // user-secrets, which reach configuration the same way) long after the config file was updated.
    [Theory]
    [MemberData(nameof(RetiredKeys))]
    public void A_retired_key_from_the_environment_fails_startup_too(string retired, string replacement)
    {
        var variable = retired.Replace(":", "__");
        Environment.SetEnvironmentVariable(variable, "something");
        try
        {
            var ex = Assert.Throws<OptionsValidationException>(() => Validate());
            Assert.Contains(retired, ex.Message);
            Assert.Contains(replacement, ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // Load-bearing, not an edge case. The un-prefixed environment-variable provider folds the whole
    // process environment into IConfiguration, and DocIntAppFactory deliberately blanks endpoint
    // keys so the offline suite cannot reach real Azure. A check that fired on presence rather than
    // on a value would reject configurations that select nothing — and would fail this repository's
    // own test suite on any machine where the old names linger empty.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_retired_key_that_carries_no_value_is_inert(string blank) =>
        Validate(("DocumentIntelligence:ApiKey", blank), ("AzureOpenAI:Endpoint", blank));

    // Exact key paths only. A check that matched on substrings would fire on unrelated
    // configuration and turn a safety net into an outage.
    [Theory]
    [InlineData("DocumentIntelligence:EndpointTimeout")]
    [InlineData("Legacy:DocumentIntelligence:ApiKey")]
    [InlineData("AzureOpenAIEndpoint")]
    [InlineData("DocInt:AzureOpenAI:ApiKey")]
    public void A_key_that_merely_resembles_a_retired_one_is_ignored(string key) =>
        Validate((key, "something"));

    [Fact]
    public void A_clean_configuration_starts() => Validate();

    /// <summary>
    /// Ordering matters as much as the check itself: a retired key and an unreachable endpoint
    /// usually arrive together, because the stale value IS the endpoint being dialled. If
    /// connectivity failed first the operator would be told the network is broken, which is the
    /// exact misdiagnosis this check exists to prevent.
    /// </summary>
    /// <remarks>
    /// Started through a real host rather than a WebApplicationFactory, for the reason OptionsTests
    /// gives: a factory boot that fails surfaces as ObjectDisposedException, which would match any
    /// startup failure at all — including the connectivity failure this test exists to rule out.
    /// app.StartAsync() gives the actual exception, which is the whole assertion.
    /// </remarks>
    [Fact]
    public async Task A_retired_key_is_reported_before_any_connectivity_failure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("DocumentIntelligence:ApiKey", "stale"),
            // An endpoint that cannot resolve, with the startup check switched on, so a
            // connectivity failure is genuinely available to be reported instead.
            new KeyValuePair<string, string?>("Foundry:DocumentIntelligenceEndpoint",
                "https://unreachable.invalid/"),
            new KeyValuePair<string, string?>($"{StartupProbeOptions.SectionName}:Enabled", "true"),
            new KeyValuePair<string, string?>($"{DependencyCheckOptions.SectionName}:Enabled", "false"),
        ]);
        builder.AddDocIntOptions();
        builder.AddStartupConnectivityCheck();
        using var app = builder.Build();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
        var message = Flatten(ex);

        Assert.Contains("DocumentIntelligence:ApiKey", message);
        Assert.Contains("Foundry:ApiKey", message);
        // The point of the test: the operator is told the configuration is stale, not that the
        // network is down. The second reading sends them somewhere the fix is not.
        Assert.DoesNotContain("Startup connectivity check failed", message);
    }

    private static string Flatten(Exception ex)
    {
        var text = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException) text.AppendLine(e.Message);
        if (ex is AggregateException aggregate)
            foreach (var inner in aggregate.Flatten().InnerExceptions) text.AppendLine(Flatten(inner));
        return text.ToString();
    }

    /// <summary>
    /// Drives validation the way OptionsTests does — through IStartupValidator rather than a
    /// WebApplicationFactory boot, because a failed factory boot surfaces as ObjectDisposedException
    /// and would pass for any startup failure at all.
    /// </summary>
    private static void Validate(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.AddDocIntOptions();
        using var app = builder.Build();
        app.Services.GetRequiredService<IStartupValidator>().Validate();
    }
}
