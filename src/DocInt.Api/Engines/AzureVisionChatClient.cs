using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace DocInt.Api.Engines;

public sealed class AzureVisionChatClient : IVisionChatClient
{
    private readonly ChatClient? _chat;

    public AzureVisionChatClient(IOptions<FoundryOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.OpenAIEndpoint)) return;
        var endpoint = new Uri(o.OpenAIEndpoint);
        var azureClient = FoundryCredential.UsesApiKey(o)
            ? new AzureOpenAIClient(endpoint, new AzureKeyCredential(o.ApiKey!))
            : new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
        _chat = azureClient.GetChatClient(o.DeploymentNameVision);
    }

    public bool IsConfigured => _chat is not null;

    public async Task<string> DescribeImageAsync(
        string systemPrompt, BinaryData image, string mediaType, CancellationToken ct)
    {
        if (_chat is null)
            throw new EngineUnconfiguredException("Foundry:OpenAIEndpoint is not configured");
        var completion = await _chat.CompleteChatAsync(
            [
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(ChatMessageContentPart.CreateImagePart(image, mediaType))
            ],
            cancellationToken: ct);
        return ExtractText(completion.Value);
    }

    /// <summary>
    /// Guards the Content[0] index: an empty/content-filtered model response carries zero
    /// content parts, which would otherwise throw ArgumentOutOfRangeException straight out of
    /// the adapter. Throwing InvalidOperationException instead gives the router's generic
    /// catch-all a clean message to map to a per-file engine_error.
    /// Public (rather than private) so it is directly unit-testable against a real ChatCompletion
    /// built via OpenAIChatModelFactory, without mocking the Azure OpenAI SDK's HTTP transport.
    /// </summary>
    public static string ExtractText(ChatCompletion completion) =>
        completion.Content.Count > 0
            ? completion.Content[0].Text
            : throw new InvalidOperationException("vision model returned no text content");
}
