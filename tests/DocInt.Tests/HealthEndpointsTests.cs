using System.Net;
using System.Text.Json;
using Azure;
using DocInt.Api.Configuration;
using DocInt.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocInt.Tests;

public class HealthEndpointsTests : IClassFixture<DocIntAppFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(DocIntAppFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Healthz_returns_healthy()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
        var names = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["self"], names);   // no endpoint configured, so no dependency checks
    }

    [Fact]
    public async Task Alive_returns_healthy()
    {
        var response = await _client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_returns_service_name()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("EuGo-docint", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Info_returns_service_metadata()
    {
        var response = await _client.GetAsync("/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("EuGo-docint", doc.RootElement.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        var endpoints = doc.RootElement.GetProperty("endpoints").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("/healthz", endpoints);
        Assert.Contains("/alive", endpoints);
        Assert.Contains("/info", endpoints);
    }
}

/// <summary>
/// A dependency that is down while the pod keeps serving. Reachable only with the startup
/// check disabled — with it on, an unreachable endpoint aborts the boot and there is no
/// running pod to ask.
/// </summary>
public class DegradedDependencyTests
{
    // The fake stands in for the Document Intelligence probe, so it must answer to the same
    // name: the monitor writes the snapshot under probe.Service, and the check registered for
    // a configured DI endpoint reads it under DocumentIntelligenceStartupProbe.ServiceName.
    // Any other name here leaves the check pinned at "not yet checked" and the test green for
    // the wrong reason.
    private static readonly string Service = DocumentIntelligenceStartupProbe.ServiceName;
    private const string Endpoint = "https://di.example/";

    private sealed class UnreachableProbe : IStartupProbe
    {
        public string Service => DocumentIntelligenceStartupProbe.ServiceName;
        public string Endpoint => DegradedDependencyTests.Endpoint;
        public Task ProbeAsync(CancellationToken ct) =>
            Task.FromException(new RequestFailedException(403, "Public access is disabled."));
    }

    private sealed class DegradedFactory : DocIntAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DocumentIntelligence:Endpoint", Endpoint);
            builder.UseSetting($"{StartupProbeOptions.SectionName}:Enabled", "false");
            // Explicitly on: the base factory blanks this to false so the monitor never dials from
            // a test that is not about it, and this test is entirely about what the monitor reports.
            builder.UseSetting($"{DependencyCheckOptions.SectionName}:Enabled", "true");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureFakes(IServiceCollection services)
        {
            services.RemoveAll<IStartupProbe>();
            services.AddSingleton<IStartupProbe, UnreachableProbe>();
        }
    }

    /// <summary>
    /// Polls until the background monitor has written its first verdict. The monitor probes
    /// immediately on start, so this settles in milliseconds; the loop exists so the test does
    /// not race the host.
    /// </summary>
    private static async Task<JsonDocument> DegradedBody(HttpClient client)
    {
        for (var i = 0; i < 50; i++)
        {
            var response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = doc.RootElement.GetProperty("status").GetString();
            var check = doc.RootElement.GetProperty("checks").EnumerateArray()
                .FirstOrDefault(c => c.GetProperty("name").GetString() == Service);
            if (status == "Degraded" && check.ValueKind is JsonValueKind.Object
                && check.TryGetProperty("reason", out var reason) && reason.GetString()!.Contains("403"))
            {
                return doc;
            }
            doc.Dispose();
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("the monitor never reported the dependency unreachable");
    }

    // THE load-bearing test. A dependency outage must not evict the pod: the endpoints are
    // shared by every replica, so a 503 here empties the Service instead of shedding load, and
    // takes the Azure-free XLSX path down with it. Fails the moment someone remaps Degraded.
    [Fact]
    public async Task An_unreachable_dependency_is_reported_but_healthz_still_answers_200()
    {
        using var factory = new DegradedFactory();
        using var doc = await DegradedBody(factory.CreateClient());

        var check = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == Service);
        Assert.Equal("Degraded", check.GetProperty("status").GetString());
        // From configuration, not from the probe: the check is constructed at registration
        // time with the endpoint the operator configured.
        Assert.Equal(Endpoint, check.GetProperty("endpoint").GetString());
        Assert.Contains("403", check.GetProperty("reason").GetString());
        Assert.True(check.TryGetProperty("lastCheckedUtc", out _));
    }

    // Liveness must not move: restarting a pod that is serving correctly fixes nothing and
    // costs a rolling outage. Guards both the missing "live" tag and the separate options object.
    [Fact]
    public async Task Alive_is_unaffected_by_a_degraded_dependency()
    {
        using var factory = new DegradedFactory();
        var client = factory.CreateClient();
        (await DegradedBody(client)).Dispose();

        var response = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
