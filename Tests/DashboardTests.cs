using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OllamaToOpenAiProxy.Tests;

public class DashboardTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DashboardTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient Client(bool allowRemote) => _factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("OPENAI_URL", "http://127.0.0.1:19191");
        builder.UseSetting("ENABLE_LOGGING", "false");
        // The test server has no client IP, which counts as remote.
        builder.UseSetting("DASHBOARD_ALLOW_REMOTE", allowRemote ? "true" : "false");
    }).CreateClient();

    [Fact]
    public async Task Dashboard_IsHidden_FromNonLocalCallers_ByDefault()
    {
        var client = Client(allowRemote: false);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dashboard/api/stats")).StatusCode);
        Assert.Equal("Ollama is running", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Dashboard_ServesPage_AndStatsIncludeRecordedRequests()
    {
        var client = Client(allowRemote: true);

        var page = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<title>Proxy Dashboard</title>", await page.Content.ReadAsStringAsync());

        await client.GetAsync("/api/version");
        var json = await client.GetFromJsonAsync<JsonObject>("/dashboard/api/stats");

        Assert.Equal("http://127.0.0.1:19191", json!["config"]!["targetUrl"]!.GetValue<string>());
        Assert.False(json["backend"]!["reachable"]!.GetValue<bool>());
        var recent = json["stats"]!["recent"]!.AsArray();
        Assert.Contains(recent, r => r!["endpoint"]!.GetValue<string>() == "/api/version");
        Assert.DoesNotContain(recent, r => r!["endpoint"]!.GetValue<string>().StartsWith("/dashboard"));
    }
}
