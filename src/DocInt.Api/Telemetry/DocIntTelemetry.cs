using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DocInt.Api.Telemetry;

public sealed class DocIntTelemetry : IDisposable
{
    public const string SourceName = "EuGo.DocInt";
    public const string MeterName = "EuGo.DocInt";
    public const string PagesProcessedInstrument = "docint.pages_processed";
    public const string FilesProcessedInstrument = "docint.files_processed";
    public const string BytesProcessedInstrument = "docint.bytes_processed";
    public const string FileDurationInstrument = "docint.file_duration";
    public const string RejectedRequestsInstrument = "docint.rejected_requests";
    public const string DuplicateFilesInstrument = "docint.duplicate_files";

    private readonly Meter _meter;

    public DocIntTelemetry(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        PagesProcessed = _meter.CreateCounter<long>(PagesProcessedInstrument, unit: "pages",
            description: "Pages (DI kinds), sheets (xlsx) or images processed per file");
        FilesProcessed = _meter.CreateCounter<long>(FilesProcessedInstrument, unit: "files",
            description: "Files processed, by kind and outcome; failures are included");
        BytesProcessed = _meter.CreateCounter<long>(BytesProcessedInstrument, unit: "By",
            description: "Bytes read per file, by kind; counted whether or not extraction succeeded");
        // Explicit boundaries, in seconds. The .NET defaults run to 10 000 and are shaped for
        // milliseconds; against a PerFileTimeoutSeconds of 100 they would drop nearly every
        // measurement into the first bucket. The top boundary sits above the timeout on purpose,
        // so a measurement that somehow exceeds it stays visible instead of being clamped.
        FileDuration = _meter.CreateHistogram<double>(FileDurationInstrument, unit: "s",
            description: "Wall-clock time to process one file, by kind and outcome",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 25, 50, 100, 250]
            });
        RejectedRequests = _meter.CreateCounter<long>(RejectedRequestsInstrument, unit: "requests",
            description: "Requests rejected as malformed (400), by reason. Not a complete count of "
                + "rejections: a body over the cap with no Content-Length is terminated by Kestrel "
                + "before this code runs");
        DuplicateFiles = _meter.CreateCounter<long>(DuplicateFilesInstrument, unit: "files",
            description: "Repeated file submissions. scope=request is exact. scope=pod is a LOWER "
                + "BOUND, not a rate: the Service load-balances across replicas, so a repeat lands "
                + "on the pod that saw it roughly 1/N of the time, and the value moves when the "
                + "HPA scales");
    }

    public ActivitySource ActivitySource { get; } = new(SourceName);

    public Counter<long> PagesProcessed { get; }
    public Counter<long> FilesProcessed { get; }
    public Counter<long> BytesProcessed { get; }
    public Histogram<double> FileDuration { get; }
    public Counter<long> RejectedRequests { get; }
    public Counter<long> DuplicateFiles { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        _meter.Dispose();
    }
}
