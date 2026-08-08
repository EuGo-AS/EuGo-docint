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
    }

    public ActivitySource ActivitySource { get; } = new(SourceName);

    public Counter<long> PagesProcessed { get; }
    public Counter<long> FilesProcessed { get; }
    public Counter<long> BytesProcessed { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        _meter.Dispose();
    }
}
