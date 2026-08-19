using System.Text.Json;
using DocInt.Api.Contracts;

namespace DocInt.Tests;

/// <summary>
/// Live smoke against real Azure. Gated: set DOCINT_LIVE_TESTS=1 plus
/// Foundry__DocumentIntelligenceEndpoint and Foundry__OpenAIEndpoint, and Foundry__ApiKey unless
/// using az login — one key for both, because both hosts belong to the same Foundry account.
/// Then run
///   DOCINT_LIVE_TESTS=1 dotnet test --no-build src/DocInt.slnx --filter "FullyQualifiedName~LiveSmokeTests"
/// Without the gate every test here reports SKIPPED.
///
/// Export these in a shell with no leftover DocumentIntelligence__* or AzureOpenAI__* variables:
/// those are retired, and the host now refuses to start while one carries a value.
/// </summary>
public class LiveSmokeTests : IClassFixture<LiveAppFactory>
{
    private static bool LiveEnabled =>
        Environment.GetEnvironmentVariable("DOCINT_LIVE_TESTS") == "1";
    // Read from the environment rather than IConfiguration on purpose: these gate whether a test
    // runs at all, which is decided before any host exists. Repointing them is not cosmetic — left
    // on the retired names they would go on answering false, and every live test would report
    // SKIPPED rather than failing, which is the one outcome nobody notices.
    private static bool HasDi =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Foundry__DocumentIntelligenceEndpoint"));
    private static bool HasVision =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Foundry__OpenAIEndpoint"));

    private readonly LiveAppFactory _factory;

    public LiveSmokeTests(LiveAppFactory factory) => _factory = factory;

    private async Task<FileResult> ExtractOne(string fixture, string contentType)
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        using var form = Multipart.Form((fixture, Golden.Bytes(fixture), contentType));
        var response = await client.PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();
        var envelope = JsonSerializer.Deserialize<ExtractResponse>(
            await response.Content.ReadAsStringAsync(), DocIntJson.Options)!;
        return envelope.Files[0];
    }

    [SkippableTheory]
    [InlineData("text.pdf", "application/pdf")]
    [InlineData("scanned.pdf", "application/pdf")]
    [InlineData("sample.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sample.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("sample.html", "text/html")]
    public async Task Layout_kinds_yield_nonempty_markdown(string fixture, string contentType)
    {
        Skip.IfNot(LiveEnabled && HasDi, "live tests disabled or Foundry__DocumentIntelligenceEndpoint not set");
        var file = await ExtractOne(fixture, contentType);
        Assert.Null(file.Error);
        Assert.False(string.IsNullOrWhiteSpace(file.Markdown));
    }

    [SkippableFact]
    public async Task Scanned_pdf_proves_ocr()
    {
        Skip.IfNot(LiveEnabled && HasDi, "live tests disabled or Foundry__DocumentIntelligenceEndpoint not set");
        var file = await ExtractOne("scanned.pdf", "application/pdf");
        Assert.Contains("OCR", file.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Photo_description_mentions_lens_or_uv_cues()
    {
        Skip.IfNot(LiveEnabled && HasVision, "live tests disabled or Foundry__OpenAIEndpoint not set");
        var file = await ExtractOne("photo.png", "image/png");
        Assert.Null(file.Error);
        Assert.False(string.IsNullOrWhiteSpace(file.ImageDescription));
        var description = file.ImageDescription!.ToLowerInvariant();
        Assert.True(description.Contains("uv") || description.Contains("lens") || description.Contains("sunglass"),
            $"description lacks lens/UV cues: {description}");
    }
}
