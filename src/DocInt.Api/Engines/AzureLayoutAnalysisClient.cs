using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Engines;

public sealed class AzureLayoutAnalysisClient : ILayoutAnalysisClient
{
    private readonly DocumentIntelligenceClient? _client;

    public AzureLayoutAnalysisClient(IOptions<FoundryOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.DocumentIntelligenceEndpoint)) return;
        var endpoint = new Uri(o.DocumentIntelligenceEndpoint);
        _client = FoundryCredential.UsesApiKey(o)
            ? new DocumentIntelligenceClient(endpoint, new AzureKeyCredential(o.ApiKey!))
            : new DocumentIntelligenceClient(endpoint, new DefaultAzureCredential());
    }

    public bool IsConfigured => _client is not null;

    public async Task<LayoutAnalysis> AnalyzeAsync(BinaryData content, CancellationToken ct)
    {
        if (_client is null)
            throw new EngineUnconfiguredException("Foundry:DocumentIntelligenceEndpoint is not configured");
        var options = new AnalyzeDocumentOptions("prebuilt-layout", content)
        {
            OutputContentFormat = DocumentContentFormat.Markdown
        };
        var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, options, ct);
        var result = operation.Value;
        return new LayoutAnalysis(
            result.Content ?? "",
            result.Pages?.Count ?? 0,
            result.Warnings?.Select(w => $"{w.Code}: {w.Message}").ToArray() ?? []);
    }
}
