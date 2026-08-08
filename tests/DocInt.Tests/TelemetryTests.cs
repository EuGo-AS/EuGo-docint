using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using DocInt.Api.Admission;
using DocInt.Api.Configuration;
using DocInt.Api.Contracts;
using DocInt.Api.Telemetry;
using DocInt.Api.Validation;
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

    // The three shapes are built here rather than inline so the Theory stays readable. Note the
    // hints_invalid case posts a VALID file part alongside the bad hints: ReadAsync checks
    // files.Count == 0 before it ever calls HintsParser, so a request carrying only bad hints is
    // rejected as no_files and this test would assert the wrong reason.
    private static HttpContent BadRequest(string reason) => reason switch
    {
        RejectReasons.NotMultipart => new StringContent("{}", Encoding.UTF8, "application/json"),
        RejectReasons.TooManyFiles => Multipart.Form(Enumerable.Range(0, 33)
            .Select(i => ($"f{i}.pdf", TestBytes.Pdf, "application/pdf")).ToArray()),
        RejectReasons.HintsInvalid => Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"))
            .WithHints("{not json"),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "no shape for this reason")
    };

    [Theory]
    [InlineData(RejectReasons.NotMultipart)]
    [InlineData(RejectReasons.TooManyFiles)]
    [InlineData(RejectReasons.HintsInvalid)]
    public async Task Rejected_requests_counts_the_400_path_by_reason(string reason)
    {
        var meterFactory = _factory.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.RejectedRequestsInstrument);

        using var content = BadRequest(reason);
        var response = await _factory.CreateClient().PostAsync("/v1/extract", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(1, measurements.Sum(m => m.Value));
        Assert.All(measurements, m => Assert.Equal(reason, m.Tags["reason"]));
    }

    // Each of these constructs its own factory rather than sharing the class fixture. The pod
    // cache is a singleton for the lifetime of a host, so a golden posted by an earlier test in
    // this class would already be in it and scope=pod would read 1 where the test expects 0 —
    // a failure that depends on test execution order, green one run and red the next.
    private static MultipartFormDataContent Bom(string name = "bom.xlsx") => Multipart.Form(
        (name, Golden.Bytes("bom.xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

    private static long ScopeTotal(IReadOnlyList<CollectedMeasurement<long>> ms, string scope) =>
        ms.Where(m => (string?)m.Tags["scope"] == scope).Sum(m => m.Value);

    // Same content, different filenames: duplicates are detected on bytes, not on names.
    [Fact]
    public async Task Duplicate_files_counts_repeats_inside_one_request()
    {
        using var factory = new ContractTestFactory();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        var bytes = Golden.Bytes("bom.xlsx");
        const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        using var form = Multipart.Form(("a.xlsx", bytes, ContentType), ("b.xlsx", bytes, ContentType));
        (await factory.CreateClient().PostAsync("/v1/extract", form)).EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(1, ScopeTotal(measurements, "request"));
        Assert.Equal(0, ScopeTotal(measurements, "pod"));
    }

    [Fact]
    public async Task Duplicate_files_counts_a_repeat_across_requests_on_the_same_pod()
    {
        using var factory = new ContractTestFactory();
        var client = factory.CreateClient();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        using (var first = Bom()) (await client.PostAsync("/v1/extract", first)).EnsureSuccessStatusCode();
        using (var second = Bom()) (await client.PostAsync("/v1/extract", second)).EnsureSuccessStatusCode();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(0, ScopeTotal(measurements, "request"));
        Assert.Equal(1, ScopeTotal(measurements, "pod"));
        // Both requests emit, including the one with nothing to report: an enabled tracker
        // produces a zero line, and only a disabled one produces no series at all.
        Assert.Equal(4, measurements.Count);
    }

    // The other half of that contract: off means silent, so a dashboard can tell "not measured"
    // from "measured, none found".
    [Fact]
    public async Task Duplicate_files_emits_nothing_when_tracking_is_disabled()
    {
        using var factory = new NoDuplicateTrackingFactory();
        var client = factory.CreateClient();
        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            DocIntTelemetry.MeterName, DocIntTelemetry.DuplicateFilesInstrument);

        using (var first = Bom()) (await client.PostAsync("/v1/extract", first)).EnsureSuccessStatusCode();
        using (var second = Bom()) (await client.PostAsync("/v1/extract", second)).EnsureSuccessStatusCode();

        Assert.Empty(collector.GetMeasurementSnapshot());
    }

    private sealed class NoDuplicateTrackingFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting($"{DuplicateTrackingOptions.SectionName}:Enabled", "false");
            base.ConfigureWebHost(builder);
        }
    }

    // A shed request is not a rejected one: docint.rejected_requests is the 400 vocabulary, and a
    // 503 is a well-formed request the pod declined to start. Separate instruments, so a dashboard
    // can tell "the caller sent nonsense" from "we ran out of room".
    [Fact]
    public async Task A_shed_request_counts_on_its_own_instrument_with_a_reason()
    {
        using var saturated = new ShedFactory();
        var meterFactory = saturated.Services.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<long>(
            meterFactory, DocIntTelemetry.MeterName, DocIntTelemetry.ShedRequestsInstrument);
        var gate = saturated.Services.GetRequiredService<RequestAdmissionGate>();
        using var held = await gate.AcquireAsync(2 * 1024 * 1024, CancellationToken.None);

        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        var response = await saturated.CreateClient().PostAsync("/v1/extract", form);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(ShedReasons.QueueTimeout, measurement.Tags["reason"]);
    }

    // Same numbers as ExtractContractTests.SaturatedFactory, and for the same reason: BudgetBytes
    // must clear MaxRequestFileBytes + 1 MiB or the host refuses to boot.
    private sealed class ShedFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("DocInt:Admission:BudgetBytes", "2097152");
            builder.UseSetting("DocInt:Admission:QueueTimeoutSeconds", "1");
            builder.UseSetting("DocInt:MaxRequestFileBytes", "1024");
            builder.UseSetting("DocInt:MaxFileBytes", "1024");
            base.ConfigureWebHost(builder);
        }
    }
}
