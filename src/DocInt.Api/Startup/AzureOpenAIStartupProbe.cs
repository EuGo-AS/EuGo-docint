using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Identity;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace DocInt.Api.Startup;

/// <summary>
/// Reaches Azure OpenAI with a one-token chat completion against the configured vision deployment.
/// The SDK has no data-plane list-deployments call, and a completion is the only probe that also
/// catches a wrong <c>DeploymentNameVision</c> — the classic misconfiguration here, which
/// otherwise shows up as a 404 on the first photo a caller sends. One token costs effectively
/// nothing per pod start. The prompt is a fixed literal: no document content ever reaches Azure
/// from a probe.
/// </summary>
public sealed class AzureOpenAIStartupProbe : IStartupProbe
{
    private const string Ping = "ping";

    private readonly IOptions<AzureOpenAIOptions> _options;
    private readonly Lazy<ChatClient> _chat;

    /// <remarks>
    /// Lazy for the same reason as DocumentIntelligenceStartupProbe: probes are constructed during
    /// hosted-service resolution, ahead of ValidateOnStart, so this constructor must not be able
    /// to throw — which includes not reading <c>options.Value</c>.
    /// </remarks>
    public AzureOpenAIStartupProbe(IOptions<AzureOpenAIOptions> options)
    {
        _options = options;
        _chat = new Lazy<ChatClient>(() => Create(options.Value));
    }

    private static ChatClient Create(AzureOpenAIOptions o)
    {
        // See DocumentIntelligenceStartupProbe: the check owns retry, so the SDK must not add its
        // own on top.
        var clientOptions = new AzureOpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries: 0) };
        var uri = new Uri(o.Endpoint!);
        var client = string.IsNullOrWhiteSpace(o.ApiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential(), clientOptions)
            : new AzureOpenAIClient(uri, new ApiKeyCredential(o.ApiKey), clientOptions);
        return client.GetChatClient(o.DeploymentNameVision);
    }

    public const string ServiceName = "Azure OpenAI";

    public string Service => ServiceName;

    /// <summary>Read after validation has passed; registered only when non-blank.</summary>
    public string Endpoint => _options.Value.Endpoint ?? "";

    public async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            await _chat.Value.CompleteChatAsync(
                [new UserChatMessage(Ping)],
                new ChatCompletionOptions { MaxOutputTokenCount = 1 },
                ct);
        }
        catch (ClientResultException ex) when (ex.Status == 400)
        {
            // The service resolved, negotiated TLS, accepted the credential and then argued with
            // the request body — a model that will not take max_completion_tokens=1, say. That is
            // connectivity proven, which is all this probe claims to measure.
        }
    }
}
