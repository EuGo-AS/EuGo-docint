using System.Text;
using DocInt.Api.Configuration;
using DocInt.Api.Contracts;
using DocInt.Api.Engines;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocInt.Api.Validation;

/// <summary>
/// Streams the multipart body: buffers each 'files' part in memory up to MaxFileBytes
/// (a too-large part is rejected at the cap, never fully buffered), collects the optional
/// 'hints' part (capped at MaxHintsBytes, same never-fully-buffered rule), detects kinds,
/// and applies purpose hints. Nothing touches disk.
/// </summary>
public sealed class MultipartExtractRequestReader(IOptions<DocIntOptions> options)
{
    /// <summary>Far beyond any legitimate hints object for &lt;=32 files; guards against unbounded buffering.</summary>
    private const int MaxHintsBytes = 262_144;

    private readonly DocIntOptions _options = options.Value;

    public async Task<IReadOnlyList<FileItem>> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is { } declared && declared > _options.MaxRequestBytes)
            throw new BadExtractRequestException(RejectReasons.BodyTooLarge,
                $"request body of {declared} bytes exceeds the limit of {_options.MaxRequestBytes} bytes");

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !"multipart/form-data".Equals(mediaType.MediaType.Value, StringComparison.OrdinalIgnoreCase))
            throw new BadExtractRequestException(RejectReasons.NotMultipart, "request must be multipart/form-data");
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            throw new BadExtractRequestException(RejectReasons.BoundaryMissing, "multipart boundary missing");

        var files = new List<FileItem>();
        // Accumulated across parts, so two files that each pass MaxFileBytes cannot together
        // exceed what the pod agreed to hold. Counts observed bytes, not retained ones: an
        // over-cap file retains nothing but still arrived and still occupied the socket.
        long acceptedBytes = 0;
        string? hintsJson = null;
        var reader = new MultipartReader(boundary, request.Body);
        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;
                var partName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;

                if (disposition.IsFileDisposition() && partName == "files")
                {
                    if (files.Count >= _options.MaxFilesPerRequest)
                        throw new BadExtractRequestException(RejectReasons.TooManyFiles,
                            $"more than {_options.MaxFilesPerRequest} files in one request");
                    var fileName = HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                    if (string.IsNullOrEmpty(fileName)) fileName = $"file-{files.Count}";
                    var (bytes, observed, tooLarge) = await BufferAsync(section.Body, _options.MaxFileBytes, ct);
                    acceptedBytes += observed;
                    if (acceptedBytes > _options.MaxRequestFileBytes)
                        throw new BadExtractRequestException(RejectReasons.RequestFilesTooLarge,
                            $"file parts total more than {_options.MaxRequestFileBytes} bytes");
                    var item = new FileItem
                    {
                        Index = files.Count,
                        Name = fileName,
                        ClaimedContentType = section.ContentType,
                        Bytes = bytes,
                        SizeBytes = observed
                    };
                    if (tooLarge)
                        item.Error = new FileError(ErrorCodes.TooLarge,
                            $"file exceeds the per-file limit of {_options.MaxFileBytes} bytes");
                    else if (bytes.Length == 0)
                        item.Error = new FileError(ErrorCodes.EmptyFile, "file is empty");
                    else
                    {
                        var detection = FileKindDetector.Detect(fileName, section.ContentType, bytes);
                        if (detection.Warning is not null) item.Warnings.Add(detection.Warning);
                        if (detection.Kind is null)
                            item.Error = new FileError(ErrorCodes.UnsupportedType,
                                $"could not detect a supported file type for '{fileName}'");
                        else
                        {
                            item.Kind = detection.Kind;
                            item.ImageMediaType = detection.ImageMediaType;
                        }
                    }
                    files.Add(item);
                }
                else if (disposition.IsFormDisposition() && partName == "hints")
                {
                    var (bytes, _, tooLarge) = await BufferAsync(section.Body, MaxHintsBytes, ct);
                    if (tooLarge)
                        throw new BadExtractRequestException(RejectReasons.HintsTooLarge,
                            $"hints part exceeds the limit of {MaxHintsBytes} bytes");
                    hintsJson = Encoding.UTF8.GetString(bytes);
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel enforcing MaxRequestBodySize on a body that declared no Content-Length, so
            // the guard at the top of this method never saw it. It cannot be preempted by counting
            // here either: Kestrel's tally is what it pulled off the socket, which runs ahead of
            // what this reader has consumed — the throw can arrive before the first section does.
            // Its exception derives from IOException, so without this case it would fall into the
            // catch below and an oversized upload would be reported as corrupt framing. Ordering
            // matters: this must precede the IOException catch, and the status test is what keeps
            // Kestrel's other 400-level rejections out of it.
            throw new BadExtractRequestException(RejectReasons.BodyTooLarge,
                $"request body exceeds the limit of {_options.MaxRequestBytes} bytes");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Multipart in content-type but truncated/corrupt in framing: MultipartReader's
            // boundary search hit an unexpected end of stream. Bad input, not a server bug.
            throw new BadExtractRequestException(RejectReasons.MalformedBody, "malformed multipart body");
        }

        if (files.Count == 0)
            throw new BadExtractRequestException(RejectReasons.NoFiles, "request contains no file parts named 'files'");
        if (hintsJson is not null)
            ApplyHints(files, HintsParser.Parse(hintsJson));
        return files;
    }

    private static void ApplyHints(List<FileItem> files, Dictionary<string, string> hints)
    {
        foreach (var file in files)
        {
            if (!hints.TryGetValue(file.Name, out var purpose)) continue;
            if (PurposeHints.Known.Contains(purpose))
                file.Purpose = purpose.ToLowerInvariant();
            else
                file.Warnings.Add($"unknown purpose hint '{purpose}' ignored");
        }
    }

    private static async Task<(byte[] Bytes, long Observed, bool TooLarge)> BufferAsync(
        Stream body, long maxBytes, CancellationToken ct)
    {
        using var buffered = new MemoryStream();
        var buffer = new byte[81920];
        long observed = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) break;
            observed += read;
            if (buffered.Length + read > maxBytes)
            {
                // Drain the rest so the reader stays in sync with the multipart framing, and keep
                // counting: the caller reports what arrived even though nothing is retained.
                int drained;
                while ((drained = await body.ReadAsync(buffer, ct)) != 0) observed += drained;
                return ([], observed, true);
            }
            buffered.Write(buffer, 0, read);
        }
        return (buffered.ToArray(), observed, false);
    }
}
