namespace DocInt.Api.Configuration;

/// <summary>
/// How the service authenticates to its Foundry account, decided once for the whole account.
/// </summary>
/// <remarks>
/// Both engine clients and both startup probes ask this instead of testing the key themselves.
/// That is the point: a Foundry account has one key pair covering every surface it exposes, so
/// "do we use a key?" is a property of the account, not of the API being called. When each of the
/// four call sites answered it separately — against what used to be two separate configuration
/// sections — the two surfaces could and did disagree, one holding a key while the other fell back
/// to DefaultAzureCredential against the same host.
///
/// The credential *types* still differ at the call sites (Azure.Core's AzureKeyCredential for
/// Document Intelligence, System.ClientModel's ApiKeyCredential for the Azure OpenAI probe), so
/// each site constructs its own; only the decision is shared, which is the part that can drift.
/// </remarks>
public static class FoundryCredential
{
    /// <summary>
    /// True when the account's key is configured, false when the ambient managed identity should
    /// be used instead. Whitespace counts as absent: a key of spaces is a guaranteed 401, and
    /// falling back is the useful reading of it.
    /// </summary>
    public static bool UsesApiKey(FoundryOptions options) => !string.IsNullOrWhiteSpace(options.ApiKey);
}
