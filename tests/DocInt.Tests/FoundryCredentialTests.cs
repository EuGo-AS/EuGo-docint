using DocInt.Api.Configuration;

namespace DocInt.Tests;

/// <summary>
/// The single decision behind "which credential do we authenticate with". It exists as one
/// expression rather than four because it used to be four: both engine clients and both startup
/// probes each ran their own blank-check, so the answer depended on which of two configuration
/// keys happened to be set — and the surface whose key was missing fell back to
/// DefaultAzureCredential against the same Azure account the other one was reaching with a key.
/// One expression, tested here, is what makes the two surfaces unable to disagree.
/// </summary>
public class FoundryCredentialTests
{
    [Fact]
    public void A_configured_key_selects_the_key_branch() =>
        Assert.True(FoundryCredential.UsesApiKey(new FoundryOptions { ApiKey = "k1" }));

    // Absent, empty and whitespace are all "no key". Whitespace matters in particular: it is what
    // an env var set to "" or a YAML value that lost its content actually looks like, and treating
    // it as a key would hand the SDK a credential guaranteed to 401.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_blank_key_selects_the_managed_identity_branch(string? key) =>
        Assert.False(FoundryCredential.UsesApiKey(new FoundryOptions { ApiKey = key }));

    // The property the whole consolidation rests on: one value decides for the whole account, so
    // asking twice — once per surface — cannot yield two different answers.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("k1")]
    public void The_same_configuration_yields_the_same_answer_every_time(string? key)
    {
        var options = new FoundryOptions
        {
            ApiKey = key,
            DocumentIntelligenceEndpoint = "https://di.example/",
            OpenAIEndpoint = "https://aoai.example/",
        };

        Assert.Equal(FoundryCredential.UsesApiKey(options), FoundryCredential.UsesApiKey(options));
    }
}
