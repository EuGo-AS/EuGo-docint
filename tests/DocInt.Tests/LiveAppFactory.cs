using DocInt.Api.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace DocInt.Tests;

/// <summary>
/// Hosts the app for the live smoke suite, against the real Foundry resource.
///
/// The base factory blanks both Foundry endpoints so the offline suite stays hermetic even on a
/// developer machine whose appsettings.Development.json carries real ones. That is correct there
/// and fatal here: the blanking wins over the environment, so a live test booted an unconfigured
/// host and failed with engine_unconfigured regardless of what was exported. This factory carries
/// the environment's values in before calling base, which leaves them alone -- Blank only fills a
/// key that is still empty.
/// </summary>
public class LiveAppFactory : DocIntAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Pass(builder, $"{FoundryOptions.SectionName}:DocumentIntelligenceEndpoint");
        Pass(builder, $"{FoundryOptions.SectionName}:OpenAIEndpoint");
        // Absent means DefaultAzureCredential, which is a supported way to run this suite, so an
        // unset key is passed through as unset rather than forced to empty.
        Pass(builder, $"{FoundryOptions.SectionName}:ApiKey");
        base.ConfigureWebHost(builder);
    }

    /// <summary>Copies Foundry__Key from the environment onto the builder, if it carries a value.</summary>
    private static void Pass(IWebHostBuilder builder, string key)
    {
        var value = Environment.GetEnvironmentVariable(key.Replace(":", "__"));
        if (!string.IsNullOrWhiteSpace(value)) builder.UseSetting(key, value);
    }
}
