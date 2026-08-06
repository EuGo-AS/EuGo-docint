using DocInt.Api.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DocInt.Tests;

/// <summary>
/// Hosts the real Program in-memory. Subclasses override ConfigureFakes to replace
/// Azure adapters / engines; the base factory runs the app exactly as configured,
/// which means no Azure endpoints are set (the engine_unconfigured path).
/// </summary>
public class DocIntAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Hermetic by default. WebApplicationFactory boots in Development and reads
        // src/DocInt.Api/appsettings.Development.json, which is untracked and on a developer
        // machine normally holds the real Azure endpoints -- so the sentence above was true in CI
        // and false locally, silently turning the unconfigured-path tests into live-Azure tests.
        // Blanking here restores it. Only where a subclass has not asked for a value: subclasses
        // call base last, so GetSetting already sees their UseSetting.
        Blank(builder, $"{DocumentIntelligenceOptions.SectionName}:Endpoint");
        Blank(builder, $"{AzureOpenAIOptions.SectionName}:Endpoint");
        // Same reason, one layer up: the startup connectivity check must not dial anything from a
        // test unless that test is about the check. StartupConnectivityCheckTests turns it back on.
        Blank(builder, $"{StartupProbeOptions.SectionName}:Enabled", "false");

        builder.ConfigureTestServices(ConfigureFakes);
    }

    private static void Blank(IWebHostBuilder builder, string key, string value = "")
    {
        if (string.IsNullOrEmpty(builder.GetSetting(key))) builder.UseSetting(key, value);
    }

    protected virtual void ConfigureFakes(IServiceCollection services)
    {
    }
}
