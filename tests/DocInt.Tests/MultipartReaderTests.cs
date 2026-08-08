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

    // A part above the cap is drained and discarded, so Bytes is empty by design. SizeBytes must
    // still report what arrived: bytes_processed and the per-file log line both read it, and a 0
    // there hides exactly the uploads that consumed the most bandwidth.
    //
    // Must be bigger than BufferAsync's 81920-byte read buffer, and comfortably so: if the whole
    // part arrives in a single ReadAsync, the cap trips on that first read, the drain loop's first
    // call returns 0 immediately, and `observed` is already correct from the pre-drain accumulation
    // alone — an implementation that reverted the drain loop to discarding the count (the old
    // `while (await body.ReadAsync(buffer, ct) != 0) { }` shape) would still pass. Only a part that
    // forces the drain loop across multiple reads actually exercises the accumulation this test
    // exists to cover. Do not "simplify" this back to a small golden fixture.
    [Fact]
    public async Task Over_cap_file_reports_the_bytes_that_actually_arrived()
    {
        var oversized = new byte[200_000];
        using var form = Multipart.Form(("big.xlsx", oversized,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var items = await Reader(TestOptions.DocInt(maxFileBytes: 1024))
            .ReadAsync(await RequestOf(form), CancellationToken.None);

        var file = Assert.Single(items);
        Assert.Equal(ErrorCodes.TooLarge, file.Error!.Code);
        Assert.Empty(file.Bytes);
        Assert.Equal(oversized.Length, file.SizeBytes);
    }

    // Control: under the cap, the observed size and the retained buffer agree.
    [Fact]
    public async Task Under_cap_file_reports_its_own_length()
    {
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var items = await Reader().ReadAsync(await RequestOf(form), CancellationToken.None);

        var file = Assert.Single(items);
        Assert.Null(file.Error);
        Assert.Equal(TestBytes.Pdf.Length, file.SizeBytes);
    }

    // Two files, each inside MaxFileBytes, whose sum is over MaxRequestFileBytes. Neither is a
    // per-file too_large, so without a running sum the pair sails through and the pod buffers
    // more than the cap allows. Checked while reading rather than only against Content-Length,
    // which chunked encoding omits entirely.
    [Fact]
    public async Task Files_summing_over_the_request_total_is_a_request_level_rejection()
    {
        using var form = Multipart.Form(
            ("a.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"),
            ("b.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"));
        var request = await RequestOf(form);
        var reader = Reader(TestOptions.DocInt(maxFileBytes: 4096, maxRequestFileBytes: 6000));

        var ex = await Assert.ThrowsAsync<BadExtractRequestException>(
            () => reader.ReadAsync(request, CancellationToken.None));

        Assert.Equal(RejectReasons.RequestFilesTooLarge, ex.Reason);
    }

    // Control: the same two files under the cap are accepted, so it is the sum that rejects and
    // not the per-file cap or the declared-size check ahead of it.
    [Fact]
    public async Task Files_summing_under_the_request_total_are_accepted()
    {
        using var form = Multipart.Form(
            ("a.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"),
            ("b.pdf", Pad(TestBytes.Pdf, 4000), "application/pdf"));
        var request = await RequestOf(form);
        var reader = Reader(TestOptions.DocInt(maxFileBytes: 4096, maxRequestFileBytes: 100_000));

        var files = await reader.ReadAsync(request, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Null(f.Error));
    }

    private static byte[] Pad(byte[] prefix, int total)
    {
        var padded = new byte[total];
        prefix.CopyTo(padded, 0);
        return padded;
    }
}
