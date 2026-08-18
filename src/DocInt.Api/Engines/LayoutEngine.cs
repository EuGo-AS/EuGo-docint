using Azure;
using DocInt.Api.Contracts;

namespace DocInt.Api.Engines;

/// <summary>PDF (incl. scanned), DOCX, PPTX, HTML → DI prebuilt-layout → Markdown.</summary>
public sealed class LayoutEngine(ILayoutAnalysisClient client) : IExtractionEngine
{
    public IReadOnlyCollection<FileKind> Kinds { get; } =
        [FileKind.Pdf, FileKind.Docx, FileKind.Pptx, FileKind.Html];

    public async Task<EngineOutcome> ExtractAsync(FileItem file, CancellationToken ct)
    {
        if (!client.IsConfigured)
            return Errors.For(file, ErrorCodes.EngineUnconfigured,
                "document layout engine is not configured: set Foundry:DocumentIntelligenceEndpoint");
        try
        {
            var analysis = await client.AnalyzeAsync(BinaryData.FromBytes(file.Bytes), ct);
            var warnings = file.Warnings.Concat(analysis.Warnings).ToArray();
            return new EngineOutcome(
                new FileResult(file.Name, file.Kind, analysis.Markdown, null, null, warnings, null),
                analysis.PageCount);
        }
        catch (RequestFailedException ex) when (ex.Status == 400)
        {
            return Errors.For(file, ErrorCodes.Corrupt,
                $"document intelligence rejected the file: {ex.ErrorCode ?? ex.Message}");
        }
    }
}
