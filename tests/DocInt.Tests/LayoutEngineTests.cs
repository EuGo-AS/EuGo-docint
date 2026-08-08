using Azure;
using DocInt.Api.Contracts;
using DocInt.Api.Engines;

namespace DocInt.Tests;

public class LayoutEngineTests
{
    private static FileItem Pdf() => new()
    { Index = 0, Name = "manual.pdf", Kind = FileKind.Pdf, Bytes = TestBytes.Pdf, SizeBytes = TestBytes.Pdf.Length };

    [Fact]
    public async Task Returns_markdown_pages_and_merged_warnings()
    {
        var engine = new LayoutEngine(new FakeLayoutClient(
            new LayoutAnalysis("# extracted", 5, ["di: low confidence region"])));
        var item = Pdf();
        item.Warnings.Add("claimed content type mismatch");
        var outcome = await engine.ExtractAsync(item, CancellationToken.None);

        Assert.Equal("# extracted", outcome.Result.Markdown);
        Assert.Equal(5, outcome.PagesProcessed);
        Assert.Contains("claimed content type mismatch", outcome.Result.Warnings);
        Assert.Contains("di: low confidence region", outcome.Result.Warnings);
        Assert.Null(outcome.Result.Error);
    }

    [Fact]
    public async Task Unconfigured_client_yields_engine_unconfigured()
    {
        var engine = new LayoutEngine(new FakeLayoutClient(configured: false));
        var outcome = await engine.ExtractAsync(Pdf(), CancellationToken.None);
        Assert.Equal(ErrorCodes.EngineUnconfigured, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task Service_content_rejection_maps_to_corrupt()
    {
        var engine = new LayoutEngine(new FakeLayoutClient(
            thrower: () => throw new RequestFailedException(400, "InvalidContent: file is damaged")));
        var outcome = await engine.ExtractAsync(Pdf(), CancellationToken.None);
        Assert.Equal(ErrorCodes.Corrupt, outcome.Result.Error!.Code);
    }
}
