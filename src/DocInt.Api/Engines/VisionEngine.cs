using DocInt.Api.Contracts;

namespace DocInt.Api.Engines;

/// <summary>JPG/PNG → one chat completion with the factual-observations guardrail.</summary>
public sealed class VisionEngine(IVisionChatClient client) : IExtractionEngine
{
    public IReadOnlyCollection<FileKind> Kinds { get; } = [FileKind.Image];

    public async Task<EngineOutcome> ExtractAsync(FileItem file, CancellationToken ct)
    {
        if (!client.IsConfigured)
            return Errors.For(file, ErrorCodes.EngineUnconfigured,
                "vision engine is not configured: set Foundry:OpenAIEndpoint");
        var description = await client.DescribeImageAsync(
            VisionPrompt.System,
            BinaryData.FromBytes(file.Bytes),
            file.ImageMediaType ?? "image/png",
            ct);
        return new EngineOutcome(
            new FileResult(file.Name, file.Kind, null, null, description, file.Warnings.ToArray(), null), 1);
    }
}
