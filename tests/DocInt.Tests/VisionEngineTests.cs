using DocInt.Api.Contracts;
using DocInt.Api.Engines;

namespace DocInt.Tests;

public class VisionEngineTests
{
    private static FileItem Png() => new()
    {
        Index = 0, Name = "photo.png", Kind = FileKind.Image, ImageMediaType = "image/png",
        Bytes = TestBytes.Png, SizeBytes = TestBytes.Png.Length
    };

    [Fact]
    public async Task Returns_description_with_pinned_prompt_and_media_type()
    {
        var fake = new FakeVisionClient("tinted lenses, UV400 sticker");
        var outcome = await new VisionEngine(fake).ExtractAsync(Png(), CancellationToken.None);

        Assert.Equal("tinted lenses, UV400 sticker", outcome.Result.ImageDescription);
        Assert.Equal(1, outcome.PagesProcessed);
        Assert.Equal(VisionPrompt.System, fake.LastSystemPrompt);
        Assert.Equal("image/png", fake.LastMediaType);
        Assert.Null(outcome.Result.Error);
    }

    [Fact]
    public async Task Unconfigured_client_yields_engine_unconfigured()
    {
        var outcome = await new VisionEngine(new FakeVisionClient(configured: false))
            .ExtractAsync(Png(), CancellationToken.None);
        Assert.Equal(ErrorCodes.EngineUnconfigured, outcome.Result.Error!.Code);
    }

    [Fact]
    public void Prompt_snapshot_pins_the_guardrail_wording()
    {
        // Deliberate double-entry bookkeeping: the guardrail cannot drift silently.
        Assert.Equal(
            "You describe product photographs for a document record. List only what is directly visible: "
            + "objects, text, markings, labels, symbols, materials, colors, quantities. Transcribe visible "
            + "text and codes exactly as printed. Do not identify product categories, do not infer purpose, "
            + "compliance status, quality, or anything not visible in the image. Output a plain-text description.",
            VisionPrompt.System);
    }
}
