using DocInt.Api.Configuration;
using DocInt.Api.Contracts;
using DocInt.Api.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

public class MultipartReaderTests
{
    private static MultipartExtractRequestReader Reader(DocIntOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? TestOptions.DocInt()));

    private static async Task<HttpRequest> RequestOf(MultipartFormDataContent form)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = form.Headers.ContentType!.ToString();
        var stream = new MemoryStream();
        await form.CopyToAsync(stream);
        stream.Position = 0;
        context.Request.Body = stream;
        context.Request.ContentLength = stream.Length;
        return context.Request;
    }

    [Fact]
    public async Task Reads_files_with_kinds_in_order()
    {
        using var form = Multipart.Form(
            ("manual.pdf", TestBytes.Pdf, "application/pdf"),
            ("photo.png", TestBytes.Png, "image/png"));
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Equal(("manual.pdf", FileKind.Pdf, 0), (items[0].Name, items[0].Kind, items[0].Index));
        Assert.Equal(("photo.png", FileKind.Image, "image/png"), (items[1].Name, items[1].Kind, items[1].ImageMediaType));
        Assert.Equal(TestBytes.Pdf, items[0].Bytes);
    }

    [Fact]
    public async Task Known_hint_sets_purpose_unknown_hint_warns()
    {
        using var form = Multipart.Form(
            ("a.pdf", TestBytes.Pdf, "application/pdf"),
            ("b.pdf", TestBytes.Pdf, "application/pdf"))
            .WithHints("""{"a.pdf":{"purpose":"bom"},"b.pdf":{"purpose":"mystery"}}""");
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);
        Assert.Equal("bom", items[0].Purpose);
        Assert.Null(items[1].Purpose);
        Assert.Contains("unknown purpose hint 'mystery' ignored", items[1].Warnings);
    }

    [Fact]
    public async Task Oversized_file_gets_too_large_error()
    {
        using var form = Multipart.Form(("big.pdf", TestBytes.Pdf, "application/pdf"));
        var items = await Reader(TestOptions.DocInt(maxFileBytes: 4)).ReadAsync(await RequestOf(form), CancellationToken.None);
        Assert.Equal(ErrorCodes.TooLarge, items[0].Error!.Code);
    }

    [Fact]
    public async Task Empty_file_gets_empty_file_error()
    {
        using var form = Multipart.Form(("empty.pdf", [], "application/pdf"));
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);
        Assert.Equal(ErrorCodes.EmptyFile, items[0].Error!.Code);
    }

    [Fact]
    public async Task Undetectable_file_gets_unsupported_type_error()
    {
        using var form = Multipart.Form(("x.bin", TestBytes.Garbage, "application/octet-stream"));
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);
        Assert.Equal(ErrorCodes.UnsupportedType, items[0].Error!.Code);
    }

    [Fact]
    public async Task Malformed_requests_throw_bad_request()
    {
        // zero files
        using var empty = new MultipartFormDataContent();
        empty.Add(new StringContent("{}"), "hints");
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader().ReadAsync(await RequestOf(empty), CancellationToken.None));

        // too many files
        using var many = Multipart.Form(
            ("a.pdf", TestBytes.Pdf, "application/pdf"),
            ("b.pdf", TestBytes.Pdf, "application/pdf"),
            ("c.pdf", TestBytes.Pdf, "application/pdf"));
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader(TestOptions.DocInt(maxFilesPerRequest: 2)).ReadAsync(await RequestOf(many), CancellationToken.None));

        // not multipart
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader().ReadAsync(context.Request, CancellationToken.None));

        // malformed hints
        using var badHints = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf")).WithHints("nope");
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader().ReadAsync(await RequestOf(badHints), CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_hints_part_throws_bad_request()
    {
        var padding = new string('a', 300_000);
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"))
            .WithHints("{\"a.pdf\":{\"purpose\":\"" + padding + "\"}}");
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader().ReadAsync(await RequestOf(form), CancellationToken.None));
    }

    [Fact]
    public async Task Declared_content_length_over_request_cap_throws_bad_request()
    {
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var request = await RequestOf(form);
        request.ContentLength = long.MaxValue;
        await Assert.ThrowsAsync<BadExtractRequestException>(async () =>
            await Reader().ReadAsync(request, CancellationToken.None));
    }
}
