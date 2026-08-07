using DocInt.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class OptionsTests
{
    // appsettings.json is the single source of truth for the limits: DocIntOptions carries no
    // property initializers, so these values can only come from the shipped config file.
    [Fact]
    public void Appsettings_supplies_the_spec_defaults()
    {
        using var factory = new DocIntAppFactory();
        var o = factory.Services.GetRequiredService<IOptions<DocIntOptions>>().Value;
        Assert.Equal(52_428_800, o.MaxFileBytes);
        Assert.Equal(32, o.MaxFilesPerRequest);
        Assert.Equal(100, o.PerFileTimeoutSeconds);
        Assert.Equal(4, o.MaxParallelism);
        Assert.Equal(52_428_800L * 32 + 1_048_576, o.MaxRequestBytes);
        Assert.Equal("model-eugo-docint-vision",
            factory.Services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value.DeploymentNameVision);
    }

    // The vision deployment name is required only once an endpoint exists: AzureVisionChatClient
    // returns early on a blank endpoint, so a name is meaningless without one. Blank-with-endpoint
    // used to reach GetChatClient(""), failing every image request; it now fails at boot instead.
    // Driven through IStartupValidator rather than WebApplicationFactory: a failed factory boot
    // surfaces as ObjectDisposedException, which would pass for any startup failure at all.
    [Fact]
    public void Blank_vision_deployment_with_endpoint_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate(
            ("AzureOpenAI:Endpoint", "https://aoai.example"),
            ("AzureOpenAI:DeploymentNameVision", "")));
        Assert.Contains("DeploymentNameVision", ex.Message);

        // Control: same endpoint, name present — proves the blank name is what fails, not the helper.
        Validate(("AzureOpenAI:Endpoint", "https://aoai.example"),
            ("AzureOpenAI:DeploymentNameVision", "model-eugo-docint-vision"));
    }

    // The chart renders these limits so a 0 reaches the pod rather than being swallowed
    // (charts/eugo-docint/templates/deployment.yaml), which is only worth doing if a 0 actually
    // stops the host. That is this test: the positivity validator was previously uncovered, so
    // the "fails at startup rather than silently falling back" contract rested on nothing.
    // Each case overrides one key; the other three keep their appsettings.json defaults, which
    // is what proves the named key is the one being rejected.
    [Theory]
    [InlineData("DocInt:MaxFileBytes")]
    [InlineData("DocInt:MaxFilesPerRequest")]
    [InlineData("DocInt:PerFileTimeoutSeconds")]
    [InlineData("DocInt:MaxParallelism")]
    public void Zero_limit_fails_host_startup(string key)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate((key, "0")));
        Assert.Contains("positive", ex.Message);

        // Control: the same helper with no override boots clean, so it is the 0 that fails.
        Validate();
    }

    // The startup check's budget has to fund the attempts it promises. A TotalTimeoutSeconds under
    // Attempts x AttemptTimeoutSeconds cancels the final attempt mid-flight — and the final attempt
    // is the one that matters, since the two before it already failed. Silent when it happens, so
    // it fails at boot instead.
    [Fact]
    public void Startup_probe_budget_below_its_own_attempts_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Validate(
            ("DocInt:StartupProbe:TotalTimeoutSeconds", "5")));
        Assert.Contains("TotalTimeoutSeconds", ex.Message);

        // Control: the shipped values satisfy it, so it is the 5 that fails and not the rule.
        Validate();
    }

    private static void Validate(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.AddDocIntOptions();
        using var app = builder.Build();
        app.Services.GetRequiredService<IStartupValidator>().Validate();
    }

    // ...and the stub-first path is untouched: no endpoint, no name, still boots.
    [Fact]
    public void Blank_vision_deployment_without_endpoint_still_boots()
    {
        using var factory = new BlankVisionDeploymentFactory(endpoint: null);
        var o = factory.Services.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
        Assert.True(string.IsNullOrEmpty(o.DeploymentNameVision));
    }

    private sealed class BlankVisionDeploymentFactory(string? endpoint) : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("AzureOpenAI:DeploymentNameVision", "");
            if (endpoint is not null) builder.UseSetting("AzureOpenAI:Endpoint", endpoint);
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void Configuration_binds_into_options()
    {
        using var factory = new BindingFactory();
        var docint = factory.Services.GetRequiredService<IOptions<DocIntOptions>>().Value;
        var di = factory.Services.GetRequiredService<IOptions<DocumentIntelligenceOptions>>().Value;
        Assert.Equal(3, docint.MaxFilesPerRequest);
        Assert.Equal("https://di.example", di.Endpoint);
    }

    private sealed class BindingFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:MaxFilesPerRequest", "3");
            builder.UseSetting("DocumentIntelligence:Endpoint", "https://di.example");
            base.ConfigureWebHost(builder);
        }
    }

    // --- Regression: a present-but-invalid endpoint used to boot cleanly and then 500 every
    // /v1/extract call (new Uri(o.Endpoint) throwing UriFormatException during DI resolution
    // of the real Azure adapter, upstream of the router's try/catch). Garbage config must now
    // fail loudly at boot instead. Absent config (the stub-first path) must keep booting fine —
    // covered by ExtractContractTests.Unconfigured_layout_engine_yields_per_file_engine_unconfigured
    // and HealthEndpointsTests.Healthz_returns_healthy, both of which run a bare DocIntAppFactory
    // with no endpoint configured at all.

    [Theory]
    [InlineData("DocumentIntelligence:Endpoint")]
    [InlineData("AzureOpenAI:Endpoint")]
    public void Malformed_endpoint_fails_host_startup(string key)
    {
        using var factory = new InvalidEndpointFactory(key);
        Assert.ThrowsAny<Exception>(() => { _ = factory.Services; });
    }

    private sealed class InvalidEndpointFactory(string key) : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting(key, "not a uri");
            base.ConfigureWebHost(builder);
        }
    }

    // A timeout at or above the interval lets one slow probe overlap the next tick, so the
    // pod ends up with two in-flight probes per dependency and a snapshot written out of
    // order. Rejected at boot rather than debugged in production.
    [Fact]
    public void A_timeout_that_does_not_fit_inside_the_interval_fails_host_startup()
    {
        using var factory = new OverlappingDependencyCheckFactory();
        Assert.ThrowsAny<Exception>(() => { _ = factory.Services; });
    }

    private sealed class OverlappingDependencyCheckFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:IntervalSeconds", "4");
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:TimeoutSeconds", "4");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void Dependency_check_defaults_bind_from_appsettings()
    {
        using var factory = new DocIntAppFactory();
        var o = factory.Services.GetRequiredService<IOptions<DependencyCheckOptions>>().Value;
        // Enabled is deliberately not asserted through the factory: DocIntAppFactory blanks it to
        // false so the monitor never dials from a test that is not about it, exactly as it already
        // does for StartupProbe:Enabled. Reading it back here would only assert the harness. Its
        // shipped-on default is covered below, where nothing sits in the way.
        Assert.Equal(30, o.IntervalSeconds);
        Assert.Equal(4, o.TimeoutSeconds);
    }

    // A bool has no "absent", so an env key that is missing or misspelled must leave the reporting
    // running rather than silently switch it off. StartupConnectivityCheckExtensions reads the same
    // key straight from configuration and mirrors this default; the two have to agree.
    [Fact]
    public void Dependency_checks_are_on_unless_something_explicitly_says_otherwise() =>
        Assert.True(new DependencyCheckOptions().Enabled);
}
