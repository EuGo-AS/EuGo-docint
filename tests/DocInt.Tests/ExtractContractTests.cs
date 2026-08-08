using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocInt.Api.Admission;
using DocInt.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DocInt.Tests;

public class ExtractContractTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;

    public ExtractContractTests(ContractTestFactory factory) => _factory = factory;

    private static async Task<ExtractResponse> ReadResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ExtractResponse>(json, DocIntJson.Options)!;
    }

    [Fact]
    public async Task Mixed_batch_returns_per_file_results_in_request_order()
    {
        using var form = Multipart.Form(
            ("manual.pdf", TestBytes.Pdf, "application/pdf"),
            ("junk.bin", TestBytes.Garbage, "application/octet-stream"),
            ("photo.png", TestBytes.Png, "image/png"));
        var result = await ReadResponse(await _factory.CreateClient().PostAsync("/v1/extract", form));

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(["manual.pdf", "junk.bin", "photo.png"], result.Files.Select(f => f.Name).ToArray());
        Assert.Equal("LAYOUT_MD_SENTINEL", result.Files[0].Markdown);
        Assert.Equal(FileKind.Pdf, result.Files[0].Kind);
        Assert.Null(result.Files[0].Error);
        Assert.Equal(ErrorCodes.UnsupportedType, result.Files[1].Error!.Code);
        Assert.Equal("VISION_DESCRIPTION_SENTINEL", result.Files[2].ImageDescription);
    }

    [Fact]
    public async Task Unknown_hint_returns_warning_on_that_file()
    {
        using var form = Multipart.Form(("manual.pdf", TestBytes.Pdf, "application/pdf"))
            .WithHints("""{"manual.pdf":{"purpose":"mystery"}}""");
        var result = await ReadResponse(await _factory.CreateClient().PostAsync("/v1/extract", form));
        Assert.Contains("unknown purpose hint 'mystery' ignored", result.Files[0].Warnings);
    }

    [Fact]
    public async Task Malformed_requests_return_400_problem()
    {
        var client = _factory.CreateClient();

        var notMultipart = await client.PostAsync("/v1/extract",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);

        using var noFiles = new MultipartFormDataContent();
        noFiles.Add(new StringContent("{}"), "hints");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/v1/extract", noFiles)).StatusCode);

        using var badHints = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf")).WithHints("nope");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/v1/extract", badHints)).StatusCode);
    }

    [Fact]
    public async Task Malformed_multipart_framing_returns_400_not_500()
    {
        var client = _factory.CreateClient();
        const string boundary = "----x";

        // multipart/form-data content type but a body that starts a section and then
        // just stops — no closing boundary, no final CRLF. This is corrupt framing,
        // not a well-formed-but-empty request: the MultipartReader must fail while
        // hunting for the boundary instead of cleanly reporting zero sections.
        var body = "--" + boundary + "\r\n"
            + "Content-Disposition: form-data; name=\"files\"; filename=\"a.pdf\"\r\n"
            + "Content-Type: application/pdf\r\n"
            + "\r\n"
            + "truncated file content with no terminating boundary, stream just ends here";
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");

        var response = await client.PostAsync("/v1/extract", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_files_returns_400()
    {
        using var capped = new CappedFactory();
        using var form = Multipart.Form(
            ("a.pdf", TestBytes.Pdf, "application/pdf"),
            ("b.pdf", TestBytes.Pdf, "application/pdf"),
            ("c.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await capped.CreateClient().PostAsync("/v1/extract", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_file_gets_per_file_too_large_inside_200()
    {
        using var tiny = new TinyFileFactory();
        using var form = Multipart.Form(
            ("big.pdf", TestBytes.Pdf, "application/pdf"),
            ("photo.png", TestBytes.Png, "image/png"));
        var result = await ReadResponse(await tiny.CreateClient().PostAsync("/v1/extract", form));
        Assert.Equal(ErrorCodes.TooLarge, result.Files[0].Error!.Code);
        Assert.Null(result.Files[1].Error);
    }

    [Fact]
    public async Task Unconfigured_layout_engine_yields_per_file_engine_unconfigured()
    {
        using var bare = new DocIntAppFactory();   // real adapters, no Azure config
        using var form = Multipart.Form(("manual.pdf", TestBytes.Pdf, "application/pdf"));
        var result = await ReadResponse(await bare.CreateClient().PostAsync("/v1/extract", form));
        Assert.Equal(ErrorCodes.EngineUnconfigured, result.Files[0].Error!.Code);
    }

    private sealed class CappedFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:MaxFilesPerRequest", "2");
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class TinyFileFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:MaxFileBytes", "20");
            base.ConfigureWebHost(builder);
        }
    }

    // A pod whose budget is already spoken for answers 503 with a Retry-After rather than
    // buffering alongside the request that holds it and OOMKilling the whole pod. The budget here
    // is two mebibytes and the queue timeout one second, so the second request cannot fit and does
    // not wait long; in production both numbers are far larger and this path is rare.
    [Fact]
    public async Task A_saturated_pod_sheds_with_503_and_a_retry_after()
    {
        using var saturated = new SaturatedFactory();
        var client = saturated.CreateClient();
        var gate = saturated.Services.GetRequiredService<RequestAdmissionGate>();

        // Hold the entire budget out-of-band, so the assertion does not depend on racing two
        // real requests against each other.
        using var held = await gate.AcquireAsync(2 * 1024 * 1024, CancellationToken.None);
        Assert.NotNull(held);

        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await client.PostAsync("/v1/extract", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    // Control: the same factory with the budget free answers 200, so it is saturation that sheds
    // and not the filter rejecting everything.
    [Fact]
    public async Task The_same_pod_with_budget_free_answers_200()
    {
        using var saturated = new SaturatedFactory();
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await saturated.CreateClient().PostAsync("/v1/extract", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The numbers here are constrained by the boot rule BudgetBytes >= MaxRequestBytes, and
    // MaxRequestBytes is MaxRequestFileBytes + 1 MiB. So a 1 MiB budget cannot work at all: with
    // MaxRequestFileBytes at 1024 the ceiling is 1 049 600, which is *above* 1 MiB, and the host
    // refuses to start. 2 MiB clears it. Permits are whole mebibytes, so the budget is 2 permits
    // and holding 2 MiB takes both, leaving a kilobyte-sized request with nothing to acquire.
    private sealed class SaturatedFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:Admission:BudgetBytes", "2097152");
            builder.UseSetting("DocInt:Admission:QueueTimeoutSeconds", "1");
            builder.UseSetting("DocInt:MaxRequestFileBytes", "1024");
            builder.UseSetting("DocInt:MaxFileBytes", "1024");
            base.ConfigureWebHost(builder);
        }
    }
}
