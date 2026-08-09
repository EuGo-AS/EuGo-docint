using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocInt.Api.Health;

/// <summary>
/// Renders the health report as JSON, so /health can say *which* dependency is unreachable
/// and why — the plain-text default carries only an aggregate word.
/// </summary>
/// <remarks>
/// The body names Azure hostnames and a one-line failure reason on an unauthenticated route.
/// That is acceptable here and only here: the service is cluster-internal with no ingress, and
/// the startup logs already record the same hostnames verbatim. No caller-supplied content can
/// reach it — probes send fixed literals.
/// </remarks>
internal static class HealthResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());
            json.WriteStartArray("checks");
            foreach (var (name, entry) in report.Entries)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteString("status", entry.Status.ToString());
                if (entry.Data.TryGetValue("endpoint", out var endpoint))
                {
                    json.WriteString("endpoint", endpoint.ToString());
                }
                if (entry.Data.TryGetValue("lastCheckedUtc", out var checkedAt)
                    && checkedAt is DateTimeOffset at)
                {
                    json.WriteString("lastCheckedUtc", at.ToUniversalTime().ToString("O"));
                }
                if (!string.IsNullOrEmpty(entry.Description))
                {
                    json.WriteString("reason", entry.Description);
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray());
    }
}
