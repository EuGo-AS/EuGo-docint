using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace DocInt.Tests;

/// <summary>
/// ServiceDefaults excludes the probe endpoints from tracing by path constant, and for most of
/// this service's life the readiness constant matched nothing: it reads "/health" while the app
/// mapped "/healthz", and StartsWithSegments compares whole segments, so every readiness probe was
/// traced — six spans a minute per pod, forever, for a request that says nothing. Renaming the
/// route to /health is what closed it, which makes this the assertion that keeps it closed: it
/// fails if either the route or the constant moves again.
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
    [InlineData("/v1/extract", true)]   // the traffic whose spans are the point
    [InlineData("/info", true)]
    [InlineData("/", true)]
    public void Only_the_probe_endpoints_are_excluded_from_tracing(string path, bool traced) =>
        Assert.Equal(traced, _filter(new DefaultHttpContext { Request = { Path = path } }));

    /// <summary>
    /// The half above cannot see the bug on its own: it asserts the filter excludes "/health",
    /// which stays true however the app is routed, so moving the route back to /healthz would
    /// leave it green. This is the other half — every excluded path must actually be served — and
    /// the two together are what say the constant and the route agree.
    /// </summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Every_excluded_path_is_a_route_that_exists(string path)
    {
        Assert.False(_filter(new DefaultHttpContext { Request = { Path = path } }),
            $"{path} is not excluded from tracing — this test is asserting the wrong path");

        var response = await _client.GetAsync(path);

        Assert.True(response.IsSuccessStatusCode,
            $"tracing excludes {path}, but nothing serves it ({(int)response.StatusCode})");
    }
}
