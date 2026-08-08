using System.Net.Http.Headers;
using System.Text;
using DocInt.Api.Configuration;
using Microsoft.Extensions.Options;

namespace DocInt.Tests;

/// <summary>
/// DocIntOptions carries no property initializers — appsettings.json owns the shipped defaults —
/// so tests that construct the options directly, bypassing configuration, must state every limit
/// they rely on. Every value below is deliberately different from the shipped one, so a fixture
/// can never be mistaken for a default and a change to appsettings.json can never leave a stale
/// copy here. The shipped values are asserted from configuration in OptionsTests.
/// </summary>
public static class TestOptions
{
    public static DocIntOptions DocInt(
        long maxFileBytes = 1_048_576,
        int maxFilesPerRequest = 8,
        int perFileTimeoutSeconds = 30,
        int maxParallelism = 2,
        long maxRequestFileBytes = 4_194_304) => new()
        {
            MaxFileBytes = maxFileBytes,
            MaxFilesPerRequest = maxFilesPerRequest,
            PerFileTimeoutSeconds = perFileTimeoutSeconds,
            MaxParallelism = maxParallelism,
            MaxRequestFileBytes = maxRequestFileBytes
        };

    public static IOptions<DocIntOptions> Wrapped(
        long maxFileBytes = 1_048_576,
        int maxFilesPerRequest = 8,
        int perFileTimeoutSeconds = 30,
        int maxParallelism = 2,
        long maxRequestFileBytes = 4_194_304) =>
        Options.Create(DocInt(maxFileBytes, maxFilesPerRequest, perFileTimeoutSeconds,
            maxParallelism, maxRequestFileBytes));
}

public static class TestBytes
{
    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% tiny fake body\n");
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    public static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00];
    public static readonly byte[] Garbage = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    public static readonly byte[] Html = Encoding.ASCII.GetBytes("  <!DOCTYPE html><html><body>x</body></html>");
}

public static class Multipart
{
    public static MultipartFormDataContent Form(params (string Name, byte[] Bytes, string ContentType)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes, contentType) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            form.Add(part, "files", name);
        }
        return form;
    }

    public static MultipartFormDataContent WithHints(this MultipartFormDataContent form, string json)
    {
        form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "hints");
        return form;
    }
}

public static class Golden
{
    public static byte[] Bytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden", name));
}
