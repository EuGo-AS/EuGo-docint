using DocInt.Api.Contracts;

namespace DocInt.Api.Engines;

/// <summary>One file of an extract request as it moves through validation → routing → engine.</summary>
public sealed class FileItem
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public string? ClaimedContentType { get; init; }
    public byte[] Bytes { get; init; } = [];

    /// <summary>
    /// Bytes read from the multipart part, including the drained remainder of an over-cap file.
    /// Not <c>Bytes.Length</c>: a too-large part is discarded, so that would report 0 for exactly
    /// the uploads that consumed the most bandwidth. Required rather than defaulted so the
    /// compiler finds every construction site instead of letting one silently report 0.
    /// </summary>
    public required long SizeBytes { get; init; }
    public FileKind? Kind { get; set; }
    public string? ImageMediaType { get; set; }
    public string? Purpose { get; set; }
    public List<string> Warnings { get; } = [];
    public FileError? Error { get; set; }
}
