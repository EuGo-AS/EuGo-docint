using System.Diagnostics;
using DocInt.Api.Configuration;
using DocInt.Api.Contracts;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace DocInt.Api.Engines;

/// <summary>Runs a batch: bounded parallelism, request-order results, per-file span + metric + log line (no content).</summary>
public sealed class ExtractionService(
    EngineRouter router,
    IOptions<DocIntOptions> options,
    DocIntTelemetry telemetry,
    ILogger<ExtractionService> logger)
{
    public async Task<ExtractResponse> ExtractAsync(IReadOnlyList<FileItem> files, CancellationToken ct)
    {
        var results = new FileResult[files.Count];
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var kindName = file.Kind?.Name() ?? "unknown";
                using var activity = telemetry.ActivitySource.StartActivity("docint.extract_file");
                activity?.SetTag("docint.kind", kindName);
                activity?.SetTag("docint.size_bytes", file.SizeBytes);

                var started = Stopwatch.GetTimestamp();
                var outcome = file.Error is not null
                    ? new EngineOutcome(new FileResult(file.Name, file.Kind, null, null, null,
                        file.Warnings.ToArray(), file.Error), 0)
                    : await router.RouteAsync(file, token);
                results[file.Index] = outcome.Result;

                var outcomeCode = outcome.Result.Error?.Code ?? "ok";
                activity?.SetTag("docint.outcome", outcomeCode);
                if (outcome.PagesProcessed > 0)
                    telemetry.PagesProcessed.Add(outcome.PagesProcessed,
                        new KeyValuePair<string, object?>("kind", kindName));
                logger.LogInformation(
                    "Processed {FileName}: kind={Kind} sizeBytes={SizeBytes} outcome={Outcome} durationMs={DurationMs:0}",
                    file.Name, kindName, file.SizeBytes, outcomeCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            });
        return new ExtractResponse(results);
    }
}
