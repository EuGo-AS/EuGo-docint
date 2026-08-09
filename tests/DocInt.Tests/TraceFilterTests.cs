using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace DocInt.Tests;

/// <summary>
/// Everything the tracing filter excludes, in one place — the two probe endpoints ServiceDefaults
/// excludes by path constant, and the scrape route Program.cs adds by wrapping that filter.
///
/// Both halves of the wrap can fail silently: land on the wrong options instance and /metrics gets
/// traced; replace instead of compose and /alive quietly returns to every trace. And for most of
/// this service's life the readiness exclusion matched nothing at all — the constant reads
/// "/health" while the app mapped "/healthz", and StartsWithSegments compares whole segments, so
/// every readiness probe was traced, six spans a minute per pod, forever, for a request that says
/// nothing. Renaming the route closed that; the second theory below is what keeps it closed.
/// </summary>
public class TraceFilterTests : IClassFixture<DocIntAppFactory>
{
    private readonly Func<HttpContext, bool> _filter;
    private readonly HttpClient _client;

    public TraceFilterTests(DocIntAppFactory factory)
    {
        _filter = factory.Services.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName).Filter!;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/health", false)]      // readiness — the kubelet's, every 10s
    [InlineData("/alive", false)]       // liveness — likewise
    [InlineData("/metrics", false)]     // the scrape, on whatever interval a scraper picks
    [InlineData("/v1/extract", true)]   // the traffic whose spans are the point
    [InlineData("/info", true)]
    [InlineData("/", true)]
    public void Only_the_probe_and_scrape_endpoints_are_excluded_from_tracing(string path, bool traced) =>
        Assert.Equal(traced, _filter(new DefaultHttpContext { Request = { Path = path } }));

    /// <summary>
    /// The theory above cannot see the bug on its own: it asserts the filter excludes "/health",
    /// which stays true however the app is routed, so moving the route back to /healthz would leave
    /// it green. This is the other half — every excluded path must actually be served — and the two
    /// together are what say the constants and the routes agree.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/metrics")]
    public async Task Every_excluded_path_is_a_route_that_exists(string path)
    {
        Assert.False(_filter(new DefaultHttpContext { Request = { Path = path } }),
            $"{path} is not excluded from tracing — this test is asserting the wrong path");

        var response = await _client.GetAsync(path);

        Assert.True(response.IsSuccessStatusCode,
            $"tracing excludes {path}, but nothing serves it ({(int)response.StatusCode})");
    }
}
