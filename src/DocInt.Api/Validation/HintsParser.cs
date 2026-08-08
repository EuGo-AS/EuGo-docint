using System.Text.Json;
using DocInt.Api.Contracts;

namespace DocInt.Api.Validation;

/// <summary>
/// A request-level rejection: the caller's request is malformed, so the whole call is a 400.
/// Distinct from a per-file <see cref="DocInt.Api.Contracts.FileError"/>, which rides inside a
/// 200. Reason is a metric tag, never part of the response body.
/// </summary>
public sealed class BadExtractRequestException(string reason, string detail) : Exception(detail)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// The closed vocabulary behind the `reason` tag on docint.rejected_requests. Every throw site of
/// <see cref="BadExtractRequestException"/> passes one of these; nothing else may become a tag
/// value, or the metric's cardinality stops being bounded.
/// </summary>
public static class RejectReasons
{
    public const string BodyTooLarge = "body_too_large";
    public const string NotMultipart = "not_multipart";
    public const string BoundaryMissing = "boundary_missing";
    public const string TooManyFiles = "too_many_files";
    public const string RequestFilesTooLarge = "request_files_too_large";
    public const string HintsTooLarge = "hints_too_large";
    public const string HintsInvalid = "hints_invalid";
    public const string MalformedBody = "malformed_body";
    public const string NoFiles = "no_files";
}

public static class HintsParser
{
    public sealed record HintEntry(string? Purpose);

    /// <summary>Returns filename → raw purpose string. Purpose validity is applied later per file.</summary>
    public static Dictionary<string, string> Parse(string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, HintEntry>>(json, DocIntJson.Options)
                ?? throw new BadExtractRequestException(RejectReasons.HintsInvalid, "hints must be a JSON object");
            return entries
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value?.Purpose))
                .ToDictionary(kv => kv.Key, kv => kv.Value!.Purpose!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw new BadExtractRequestException(RejectReasons.HintsInvalid,
                "hints part is not a valid JSON object of {\"<filename>\":{\"purpose\":\"...\"}}");
        }
    }
}
