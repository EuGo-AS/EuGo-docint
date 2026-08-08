using System.Diagnostics;
using System.IO.Hashing;
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
    DuplicateFileTracker tracker,
    ILogger<ExtractionService> logger)
{
    public async Task<ExtractResponse> ExtractAsync(IReadOnlyList<FileItem> files, CancellationToken ct)
    {
        var results = new FileResult[files.Count];
        // Indexed by file.Index, null where the file is excluded from tracking. Kept local rather
        // than on FileItem: a hash exists only to feed a counter, and FileItem models the file as
        // it moves through validation and routing.
        var hashes = new ulong?[files.Count];
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelism, CancellationToken = ct },
            async (file, token) =>
            {
                var kindName = file.Kind?.Name() ?? "unknown";
                using var activity = telemetry.ActivitySource.StartActivity("docint.extract_file");
                activity?.SetTag("docint.kind", kindName);
                activity?.SetTag("docint.size_bytes", file.SizeBytes);

                // Hashed here rather than in a pass before the loop. The pod scope needs every
                // accepted file hashed — a file unique within its batch still has to be checked
                // against the cache — so grouping by length first saves nothing, and 32 x 50 MiB
                // on the critical path is ~300ms single-threaded. In here it runs at
                // MaxParallelism and, for every engine but the synchronous SpreadsheetEngine,
                // hides behind an Azure round-trip.
                // Files with an Error are excluded and this is load-bearing: a too_large file
                // carries an empty Bytes, so without the check every over-cap file in a batch
                // would hash alike and be reported as a duplicate of the others.
                if (tracker.Enabled && file.Error is null)
                    hashes[file.Index] = XxHash64.HashToUInt64(file.Bytes);

                var started = Stopwatch.GetTimestamp();
                var outcome = file.Error is not null
                    ? new EngineOutcome(new FileResult(file.Name, file.Kind, null, null, null,
                        file.Warnings.ToArray(), file.Error), 0)
                    : await router.RouteAsync(file, token);
                results[file.Index] = outcome.Result;

                var outcomeCode = outcome.Result.Error?.Code ?? "ok";
                activity?.SetTag("docint.outcome", outcomeCode);
                var elapsed = Stopwatch.GetElapsedTime(started);

                var kindTag = new KeyValuePair<string, object?>("kind", kindName);
                var outcomeTag = new KeyValuePair<string, object?>("outcome", outcomeCode);
                if (outcome.PagesProcessed > 0)
                    telemetry.PagesProcessed.Add(outcome.PagesProcessed, kindTag);
                // Unconditional, unlike PagesProcessed above — do not copy that guard here. A
                // "files processed" total that drops failures is not a total, and bytes must count
                // what was read, not what came back out.
                telemetry.FilesProcessed.Add(1, kindTag, outcomeTag);
                telemetry.BytesProcessed.Add(file.SizeBytes, kindTag);
                telemetry.FileDuration.Record(elapsed.TotalSeconds, kindTag, outcomeTag);
                logger.LogInformation(
                    "Processed {FileName}: kind={Kind} sizeBytes={SizeBytes} outcome={Outcome} durationMs={DurationMs:0}",
                    file.Name, kindName, file.SizeBytes, outcomeCode,
                    elapsed.TotalMilliseconds);
            });
        // After the loop, so it is skipped when the loop throws — EngineRouter rethrows on genuine
        // request abandonment, and an abandoned request has no meaningful outcome to report.
        if (tracker.Enabled)
        {
            var counts = tracker.Record([.. hashes.Where(h => h.HasValue).Select(h => h!.Value)]);
            // Emitted even at zero: an enabled tracker with nothing to report must produce a flat
            // zero line, so a dashboard can tell it apart from a tracker that is switched off.
            telemetry.DuplicateFiles.Add(counts.WithinRequest,
                new KeyValuePair<string, object?>("scope", "request"));
            telemetry.DuplicateFiles.Add(counts.AcrossRequests,
                new KeyValuePair<string, object?>("scope", "pod"));
        }
        return new ExtractResponse(results);
    }
}
