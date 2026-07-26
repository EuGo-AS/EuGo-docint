using System.Net;
using System.Text.Json;

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
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Alive_returns_healthy()
    {
        var response = await _client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
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
