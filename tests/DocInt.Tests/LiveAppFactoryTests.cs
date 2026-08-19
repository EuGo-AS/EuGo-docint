using DocInt.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// The live suite hosts the app through <see cref="LiveAppFactory"/> rather than the base factory,
/// because the base one blanks both Foundry endpoints on purpose. That blanking is what keeps the
/// offline suite hermetic, and it silently neutered every live test: the host booted unconfigured,
/// so each one failed with engine_unconfigured no matter what the environment said. It went
/// unnoticed because the live suite is env-gated and the endpoints were unreachable anyway.
/// </summary>
public class LiveAppFactoryTests
{
    [Fact]
    public void Live_factory_carries_the_environment_endpoints_into_configuration()
    {
        const string di = "https://live-factory-test.cognitiveservices.azure.com/";
        const string oai = "https://live-factory-test.openai.azure.com/";
        var priorDi = Environment.GetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint");
        var priorOai = Environment.GetEnvironmentVariable("Foundry__OpenAIEndpoint");
        try
        {
            Environment.SetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint", di);
            Environment.SetEnvironmentVariable("Foundry__OpenAIEndpoint", oai);

            using var factory = new LiveAppFactory();
            var options = factory.Services.GetRequiredService<IOptions<FoundryOptions>>().Value;

            Assert.Equal(di, options.DocumentIntelligenceEndpoint);
            Assert.Equal(oai, options.OpenAIEndpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint", priorDi);
            Environment.SetEnvironmentVariable("Foundry__OpenAIEndpoint", priorOai);
        }
    }

    [Fact]
    public void Base_factory_still_blanks_the_endpoints()
    {
        const string di = "https://should-be-ignored.cognitiveservices.azure.com/";
        var prior = Environment.GetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint");
        try
        {
            Environment.SetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint", di);

            using var factory = new DocIntAppFactory();
            var options = factory.Services.GetRequiredService<IOptions<FoundryOptions>>().Value;

            Assert.True(string.IsNullOrEmpty(options.DocumentIntelligenceEndpoint));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint", prior);
        }
    }
}
