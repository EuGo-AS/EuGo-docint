using System.Diagnostics.Metrics;
using DocInt.Api.Contracts;
using DocInt.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace DocInt.Tests;

public class TelemetryTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;

    public TelemetryTests(ContractTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Pages_processed_metric_counts_sheets_with_kind_tag()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.PagesProcessedInstrument);

        using var form = Multipart.Form(("bom.xlsx", Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Sum(m => m.Value));   // BoM + Notes sheets
        Assert.All(measurements, m => Assert.Equal("xlsx", m.Tags["kind"]));
    }

    // The three fixtures cover the three shapes a file can take: extracted (bom.xlsx -> ok),
    // reached an engine and failed (corrupt.xlsx -> corrupt), and rejected by the reader before
    // any engine ran (unknown.bin -> unsupported_type, kind unknown).
    private static MultipartFormDataContent MixedBatch() => Multipart.Form(
        ("bom.xlsx", Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("corrupt.xlsx", Golden.Bytes("corrupt.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ("unknown.bin", Golden.Bytes("unknown.bin"), "application/octet-stream"));

    // Failures count. A "files processed" total that silently drops the corrupt and the
    // unsupported file is not a total, and this is the assertion that keeps the
    // `if (PagesProcessed > 0)` guard from being copied onto this counter.
    [Fact]
    public async Task Files_processed_counts_every_file_including_failures()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.FilesProcessedInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var byTag = collector.GetMeasurementSnapshot()
            .ToDictionary(m => ($"{m.Tags["kind"]}/{m.Tags["outcome"]}"), m => m.Value);
        Assert.Equal(3, byTag.Values.Sum());
        Assert.Equal(1, byTag["xlsx/ok"]);
        Assert.Equal(1, byTag[$"xlsx/{ErrorCodes.Corrupt}"]);
        Assert.Equal(1, byTag[$"unknown/{ErrorCodes.UnsupportedType}"]);
    }

    // Bytes read, not bytes successfully extracted: a corrupt file that reports zero would make
    // this useless as a capacity signal.
    [Fact]
    public async Task Bytes_processed_counts_what_was_read_from_every_file()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.BytesProcessedInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var expected = Golden.Bytes("bom.xlsx").Length
                     + Golden.Bytes("corrupt.xlsx").Length
                     + Golden.Bytes("unknown.bin").Length;
        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(expected, measurements.Sum(m => m.Value));
        Assert.All(measurements, m => Assert.Contains(m.Tags["kind"], new[] { "xlsx", "unknown" }));
    }

    // Tags and one measurement per file — deliberately no assertion on magnitude, which is
    // timing-dependent and would flake in CI.
    [Fact]
    public async Task File_duration_records_one_measurement_per_file_with_kind_and_outcome()
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<double>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.FileDurationInstrument);

        using var form = MixedBatch();
        var response = await _factory.CreateClient().PostAsync("/v1/extract", form);
        response.EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(3, measurements.Count);
        Assert.All(measurements, m =>
        {
            Assert.True(m.Value >= 0);
            Assert.NotNull(m.Tags["kind"]);
            Assert.NotNull(m.Tags["outcome"]);
        });
    }
}
