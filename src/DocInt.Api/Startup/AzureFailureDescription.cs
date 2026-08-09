using System.ClientModel;
using Azure;
using Polly.Timeout;

namespace DocInt.Api.Startup;

/// <summary>
/// One line, status first. Azure's exception messages run to many lines and can echo the
/// request back; only the summary line belongs in a log or in a health report.
/// </summary>
/// <remarks>
/// Extracted from <see cref="StartupConnectivityCheck"/> once the periodic
/// <c>DependencyHealthMonitor</c> needed the identical rendering: the boot-time failure and
/// the running-service failure must read the same, or an operator comparing a pod log to a
/// /health body sees two descriptions of one fault.
/// </remarks>
internal static class AzureFailureDescription
{
    public static string Describe(Exception ex) => ex switch
    {
        RequestFailedException r => $"HTTP {r.Status}{Code(r.ErrorCode)}: {FirstLine(r.Message)}",
        ClientResultException c => $"HTTP {c.Status}: {FirstLine(c.Message)}",
        TimeoutRejectedException or OperationCanceledException => "timed out",
        _ => $"{ex.GetType().Name}: {FirstLine(ex.Message)}",
    };

    private static string Code(string? errorCode) =>
        string.IsNullOrEmpty(errorCode) ? "" : $" {errorCode}";

    private static string FirstLine(string message)
    {
        var line = message.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0) line = line[..end];
        return line.Length > 200 ? string.Concat(line[..200], "…") : line.ToString();
    }
}
