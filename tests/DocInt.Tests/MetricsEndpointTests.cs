using System.Net;
using System.Text.Json;
using DocInt.Api.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace DocInt.Tests;

/// <summary>
/// The Prometheus scraping route. It is a second reader on the same meter as the OTLP exporter,
/// so these tests are about the route existing and rendering — the numbers themselves are
/// <see cref="TelemetryTests"/>' job.
/// </summary>
public class MetricsEndpointTests : IClassFixture<ContractTestFactory>
{
    private readonly ContractTestFactory _factory;

    public MetricsEndpointTests(ContractTestFactory factory) => _factory = factory;

    /// <summary>
    /// An instrument is absent from the exposition until it has recorded at least once, so this
    /// drives a real extract first. Asserting only the status code would pass against an endpoint
    /// that renders nothing.
    /// </summary>
    [Fact]
    public async Task Metrics_exposes_the_docint_instruments_after_a_request()
    {
        var client = _factory.CreateClient();
        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        (await client.PostAsync("/v1/extract", form)).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        // Prometheus names, not OTel names: the exporter lowercases, replaces '.' with '_' and
        // appends the unit and type suffixes. If this assertion starts failing the dashboards
        // break too — the translated name is the user-visible contract, see README § Telemetry.
        Assert.Contains("# TYPE docint_files_processed_files_total counter", body);
        Assert.Contains("docint_files_processed_files_total{", body);
        Assert.Contains("# TYPE docint_file_duration_seconds histogram", body);
    }

    /// <summary>The route names itself in /info, or an operator has to read the source to find it.</summary>
    [Fact]
    public async Task Info_lists_the_metrics_route()
    {
        var response = await _factory.CreateClient().GetAsync("/info");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = doc.RootElement.GetProperty("endpoints").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        Assert.Contains("/metrics", endpoints);
    }
}

// The scrape route's own tracing exclusion is asserted in TraceFilterTests, alongside the probe
// endpoints' — one filter, one place, and that class also checks every excluded path is served.

/// <summary>
/// The scrape route and the OTLP exporter are two readers on one MeterProvider, and Prometheus
/// requires cumulative temporality while the OTLP side is configured independently. Readers own
/// their own aggregation state, so the two are supposed to be blind to each other — this asserts
/// it, because the failure mode is silent: a scrape that renders instruments with zeros while the
/// collector shows real traffic, which reads as an idle pod rather than as a broken export.
/// </summary>
public class MetricsAlongsideOtlpTests
{
    private sealed class OtlpFactory : ContractTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Nothing listens on it. The endpoint being set is the whole point: that is what makes
            // ServiceDefaults call UseOtlpExporter and register the second reader. Export attempts
            // fail in the background and are irrelevant to what is asserted here.
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4317");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public async Task The_scrape_still_reports_real_values_with_the_otlp_reader_active()
    {
        using var factory = new OtlpFactory();
        var client = factory.CreateClient();
        var before = FilesProcessed(await Scrape(client));

        using var form = Multipart.Form(("a.pdf", TestBytes.Pdf, "application/pdf"));
        (await client.PostAsync("/v1/extract", form)).EnsureSuccessStatusCode();

        // A delta, not an absolute count. System.Diagnostics.Metrics matches meters by NAME across
        // the whole process, so this host's MeterProvider also aggregates whatever the other test
        // hosts' "EuGo.DocInt" meters publish while it is listening -- an absolute assertion here
        // read whichever other extract tests happened to be in flight, and failed as 4 != 1. Noise
        // can only inflate the delta, never suppress this request's own measurement, so >= 1 is
        // both stable and still the thing worth asserting: a reader that never received the
        // measurement renders the series at its old value and the delta is 0.
        var after = FilesProcessed(await Scrape(client));

        Assert.True(after - before >= 1,
            $"the scrape did not see the request: {before} -> {after}");
    }

    // No caching to defeat: the exporter is registered with ScrapeResponseCacheDurationMilliseconds
    // at 0, so the second scrape re-renders rather than replaying the first.
    private static async Task<string> Scrape(HttpClient client) =>
        await (await client.GetAsync("/metrics")).Content.ReadAsStringAsync();

    /// <summary>Sums the series, since the tag set varies with how the file was handled.</summary>
    private static int FilesProcessed(string exposition) => exposition.Split('\n')
        .Where(l => l.StartsWith("docint_files_processed_files_total{"))
        .Sum(l => int.Parse(l.Split(' ')[^1].Trim()));
}

/// <summary>
/// The off switch has to remove the route, not just stop the exporter: a 200 rendering nothing
/// reads as "no traffic" on a dashboard, which is the one answer worse than a 404.
/// </summary>
public class MetricsDisabledTests
{
    private sealed class NoMetricsFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting($"{MetricsOptions.SectionName}:Enabled", "false");
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public async Task Metrics_is_not_mapped_when_disabled()
    {
        using var factory = new NoMetricsFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);

        using var doc = JsonDocument.Parse(await (await client.GetAsync("/info")).Content.ReadAsStringAsync());
        var endpoints = doc.RootElement.GetProperty("endpoints").EnumerateArray()
            .Select(e => e.GetString()).ToArray();
        Assert.DoesNotContain("/metrics", endpoints);
    }
}
