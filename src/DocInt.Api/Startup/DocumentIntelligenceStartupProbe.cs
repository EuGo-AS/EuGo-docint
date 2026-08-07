using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Startup;

/// <summary>
/// Reaches Document Intelligence through <c>GET /documentintelligence/info</c>, the cheapest call
/// the service has: no document, no page charge, but a full round trip through DNS, TLS and the
/// credential. There is nothing to check on the model side — prebuilt-layout is built in and named
/// in the request body, never provisioned — so resource details is as close to a ping as it gets.
/// </summary>
public sealed class DocumentIntelligenceStartupProbe : IStartupProbe
{
    private readonly IOptions<DocumentIntelligenceOptions> _options;
    private readonly Lazy<DocumentIntelligenceAdministrationClient> _client;

    /// <remarks>
    /// Nothing here may throw, and that rules out touching <c>options.Value</c>: the host resolves
    /// its hosted services — and through them every IStartupProbe — before ValidateOnStart runs,
    /// so both binding the options and parsing the URI have to wait. Doing either eagerly raises
    /// the failure mid-resolution, which pre-empts the validator's clean "must be an absolute URI"
    /// message and leaves Host with no hosted-service list, turning disposal into its own error.
    /// </remarks>
    public DocumentIntelligenceStartupProbe(IOptions<DocumentIntelligenceOptions> options)
    {
        _options = options;
        _client = new Lazy<DocumentIntelligenceAdministrationClient>(() => Create(options.Value));
    }

    public const string ServiceName = "Document Intelligence";

    public string Service => ServiceName;

    /// <summary>Read after validation has passed; registered only when non-blank.</summary>
    public string Endpoint => _options.Value.Endpoint ?? "";

    public Task ProbeAsync(CancellationToken ct) => _client.Value.GetResourceDetailsAsync(ct);

    private static DocumentIntelligenceAdministrationClient Create(DocumentIntelligenceOptions o)
    {
        // MaxRetries = 0 because the check owns retry: leaving the SDK's default 3 in place would
        // turn the configured 3 attempts into 9-16 and blow past the pod's startup window.
        var clientOptions = new DocumentIntelligenceClientOptions { Retry = { MaxRetries = 0 } };
        var uri = new Uri(o.Endpoint!);
        return string.IsNullOrWhiteSpace(o.ApiKey)
            ? new DocumentIntelligenceAdministrationClient(uri, new DefaultAzureCredential(), clientOptions)
            : new DocumentIntelligenceAdministrationClient(uri, new AzureKeyCredential(o.ApiKey), clientOptions);
    }
}
