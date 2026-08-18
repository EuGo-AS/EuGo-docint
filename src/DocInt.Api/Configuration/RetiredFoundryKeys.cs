using Microsoft.Extensions.Options;

namespace DocInt.Api.Configuration;

/// <summary>
/// Fails the service at boot when configuration still carries one of the retired per-surface keys,
/// naming the Foundry key that replaces it.
/// </summary>
/// <remarks>
/// Retiring a configuration key silently is the dangerous half of this rename. An unbound
/// <c>DocumentIntelligence:ApiKey</c> is not an error to the options system — it is simply not read
/// — and an absent key already means something specific here: use DefaultAzureCredential. So a
/// stale value would leave the service authenticating by managed identity against an account it
/// used to reach with a key, and the first report of it would be a 401 or an unreachable endpoint.
/// That sends an operator to the network, which is the wrong place.
///
/// Implemented as an <see cref="IValidateOptions{TOptions}"/> over <see cref="FoundryOptions"/> so
/// it fails through the same channel as every other configuration error in this service — one
/// exception, before Kestrel binds — rather than inventing a second failure mode.
/// </remarks>
public sealed class RetiredFoundryKeys(IConfiguration configuration) : IValidateOptions<FoundryOptions>
{
    /// <summary>
    /// Retired key to its replacement. Both ApiKey entries point at the same Foundry key on
    /// purpose: they always were the same secret, which is the whole reason for this change.
    /// </summary>
    private static readonly (string Retired, string Replacement)[] Keys =
    [
        ("DocumentIntelligence:Endpoint", $"{FoundryOptions.SectionName}:DocumentIntelligenceEndpoint"),
        ("DocumentIntelligence:ApiKey", $"{FoundryOptions.SectionName}:ApiKey"),
        ("AzureOpenAI:Endpoint", $"{FoundryOptions.SectionName}:OpenAIEndpoint"),
        ("AzureOpenAI:ApiKey", $"{FoundryOptions.SectionName}:ApiKey"),
        ("AzureOpenAI:DeploymentNameVision", $"{FoundryOptions.SectionName}:DeploymentNameVision"),
    ];

    public ValidateOptionsResult Validate(string? name, FoundryOptions options)
    {
        // Indexed lookups, never a scan: an exact key path cannot fire on an unrelated variable,
        // and the whole process environment is in here.
        //
        // A blank value is inert and must stay that way. DocIntAppFactory blanks endpoint keys on
        // purpose, environments carry empty variables, and a value that selects nothing changes no
        // behaviour — failing on presence alone would reject harmless configurations and, worse,
        // train people to disable the check.
        var stale = Keys
            .Where(k => !string.IsNullOrWhiteSpace(configuration[k.Retired]))
            .Select(k => $"{k.Retired} is retired and is no longer read; set {k.Replacement} instead "
                       + "(one Foundry account, one key pair, serving both endpoints)")
            .ToArray();

        return stale.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(stale);
    }
}
