using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OllamaToOpenAiProxy.Tests;

public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProxySettings:OpenAiUrl", "http://127.0.0.1:19191");
            builder.UseSetting("ProxySettings:LmStudioUrl", "http://127.0.0.1:19191");
            builder.UseSetting("ProxySettings:EnableLogging", "false");
            builder.UseSetting("OPENAI_URL", "http://127.0.0.1:19191");
            builder.UseSetting("LMSTUDIO_URL", "http://127.0.0.1:19191");
        }).CreateClient();
    }

    [Fact]
    public async Task Root_ReturnsOllamaRunning()
    {
        var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal("Ollama is running", content);
    }

    [Fact]
    public async Task Version_ReturnsVersionJson()
    {
        var response = await _client.GetAsync("/api/version");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("0.5.12", json["version"]?.ToString());
    }

    [Fact]
    public async Task Tags_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.NotNull(json["models"]);
    }

    [Fact]
    public async Task Tags_Post_ReturnsJsonResponse()
    {
        var response = await _client.PostAsync("/api/tags", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Show_WithoutModel_ReturnsBadRequest()
    {
        var response = await _client.GetAsync("/api/show");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Show_WithModelName_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/api/show?name=test-model");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
    }

    [Fact]
    public async Task Show_Post_WithModelName_ReturnsJsonResponse()
    {
        var body = new { name = "test-model" };
        var response = await _client.PostAsJsonAsync("/api/show", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ps_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/api/ps");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.NotNull(json["models"]);
    }

    [Fact]
    public async Task Chat_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Hello" }
            },
            stream = false
        };
        var response = await _client.PostAsJsonAsync("/api/chat", body);
        // Should return 200 or 502 (if LM Studio not running), not 400
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithInvalidBody_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/chat", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Generate_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            prompt = "Hello",
            stream = false
        };
        var response = await _client.PostAsJsonAsync("/api/generate", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Embeddings_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            prompt = "Hello world"
        };
        var response = await _client.PostAsJsonAsync("/api/embeddings", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Embed_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            input = "Hello world"
        };
        var response = await _client.PostAsJsonAsync("/api/embed", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task V1Models_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.NotNull(json["data"]);
    }

    [Fact]
    public async Task Models_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task V1ModelsById_ReturnsJsonResponse()
    {
        var response = await _client.GetAsync("/v1/models/test-model");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(json);
        Assert.Equal("test-model", json["id"]?.ToString());
    }

    [Fact]
    public async Task V1ChatCompletions_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            messages = new[]
            {
                new { role = "user", content = "Hello" }
            }
        };
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task V1Completions_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            prompt = "Hello"
        };
        var response = await _client.PostAsJsonAsync("/v1/completions", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task V1Embeddings_WithValidBody_ReturnsResponse()
    {
        var body = new
        {
            model = "test-model",
            input = "Hello world"
        };
        var response = await _client.PostAsJsonAsync("/v1/embeddings", body);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pull_ReturnsSuccessResponse()
    {
        var body = new { name = "test-model" };
        var response = await _client.PostAsJsonAsync("/api/pull", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pull_ReturnsNdjsonContent()
    {
        var body = new { name = "test-model" };
        var response = await _client.PostAsJsonAsync("/api/pull", body);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Show_Post_WithoutModel_ReturnsBadRequest()
    {
        var body = new { };
        var response = await _client.PostAsJsonAsync("/api/show", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}