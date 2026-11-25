using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy.Tests;

public class ProxyStatsTests
{
    private static long Later(long from, double seconds) => from + (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Record_ComputesTokensPerSecond_FromFirstTokenToEnd()
    {
        var stats = new ProxyStats();
        var metrics = new RequestMetrics { CompletionTokens = 100, PromptTokens = 20 };
        metrics.MarkFirstToken();
        long end = Later(metrics.FirstTokenTimestamp!.Value, 2.0);

        var record = stats.Record("POST", "/api/chat", 200, metrics, end);

        Assert.Equal(50.0, record.TokensPerSecond);
        var snap = stats.Snapshot();
        Assert.Equal(20, snap["promptTokens"]!.GetValue<long>());
        Assert.Equal(100, snap["completionTokens"]!.GetValue<long>());
        Assert.Equal(50.0, snap["avgTokensPerSecond"]!.GetValue<double>());
    }

    [Fact]
    public void Record_LeavesTokensPerSecondEmpty_WithoutStreamingOrUsage()
    {
        var stats = new ProxyStats();
        Assert.Null(stats.Record("POST", "/api/chat", 200, new RequestMetrics { CompletionTokens = 10 }).TokensPerSecond);

        var noUsage = new RequestMetrics();
        noUsage.MarkFirstToken();
        Assert.Null(stats.Record("POST", "/api/chat", 200, noUsage).TokensPerSecond);
        Assert.Null(stats.Snapshot()["avgTokensPerSecond"]);
    }

    [Fact]
    public void Recent_IsCapped_NewestFirst_AndEndpointsCountErrors()
    {
        var stats = new ProxyStats();
        for (int i = 0; i < ProxyStats.Capacity + 5; i++)
            stats.Record("GET", "/api/tags", i % 2 == 0 ? 200 : 502, new RequestMetrics());
        stats.Record("POST", "/api/chat", 200, new RequestMetrics());

        var snap = stats.Snapshot();
        var recent = snap["recent"]!.AsArray();
        Assert.Equal(ProxyStats.Capacity, recent.Count);
        Assert.Equal("/api/chat", recent[0]!["endpoint"]!.GetValue<string>());
        Assert.Equal(ProxyStats.Capacity + 6, snap["requests"]!.GetValue<long>());

        var tags = snap["endpoints"]!.AsArray().First(e => e!["endpoint"]!.GetValue<string>() == "/api/tags")!;
        Assert.Equal(ProxyStats.Capacity + 5, tags["count"]!.GetValue<long>());
        Assert.Equal(102, tags["errors"]!.GetValue<long>());
        Assert.Equal(102, snap["errors"]!.GetValue<long>());
    }

    [Fact]
    public void ModelContextLength_ReadsKnownFieldNames_PreferringLoadedSize()
    {
        Assert.Equal(4096, RequestMetrics.ModelContextLength(JsonNode.Parse(
            """{"max_context_length":131072,"loaded_instances":[{"config":{"context_length":4096}}]}""")!.AsObject()));
        Assert.Equal(32768, RequestMetrics.ModelContextLength(JsonNode.Parse("""{"max_model_len":32768}""")!.AsObject()));
        Assert.Equal(8192, RequestMetrics.ModelContextLength(JsonNode.Parse("""{"loaded_instances":[],"context_length":8192}""")!.AsObject()));
        Assert.Null(RequestMetrics.ModelContextLength(JsonNode.Parse("""{"id":"m"}""")!.AsObject()));
        Assert.Null(RequestMetrics.ModelContextLength(null));
    }

    [Fact]
    public void AddDoneFields_WithMetrics_AddsDurationsAndHandsOverTokens()
    {
        long start = Stopwatch.GetTimestamp();
        var metrics = new RequestMetrics();
        metrics.MarkFirstToken();
        var done = OllamaTranslator.AddDoneFields(new JsonObject(), "stop",
            JsonNode.Parse("""{"prompt_tokens":5,"completion_tokens":9}"""), start, metrics);

        Assert.NotNull(done["eval_duration"]);
        Assert.NotNull(done["prompt_eval_duration"]);
        Assert.Equal(5, metrics.PromptTokens);
        Assert.Equal(9, metrics.CompletionTokens);
    }

    [Theory]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("::1", false, true)]
    [InlineData("::ffff:127.0.0.1", false, true)]
    [InlineData("192.168.1.20", false, false)]
    [InlineData("192.168.1.20", true, true)]
    public void Dashboard_IsAllowed_OnlyLoopbackUnlessRemoteAllowed(string ip, bool allowRemote, bool expected)
    {
        Assert.Equal(expected, Dashboard.IsAllowed(IPAddress.Parse(ip), allowRemote));
    }

    [Fact]
    public void Dashboard_IsAllowed_DeniesUnknownAddress()
    {
        Assert.False(Dashboard.IsAllowed(null, allowRemote: false));
    }

    [Fact]
    public void Dashboard_RedactUrl_RemovesCredentials()
    {
        Assert.Equal("http://host:1234/", Dashboard.RedactUrl("http://user:secret@host:1234"));
        Assert.Equal("http://localhost:1234", Dashboard.RedactUrl("http://localhost:1234"));
    }

    [Fact]
    public void GpuStats_ParsesNvidiaSmiCsv_AndToleratesMissingValues()
    {
        var gpus = GpuStats.Parse("NVIDIA GeForce RTX 4090, 37, 10240, 24564, 61\nTesla T4, [N/A], 512, 15360, [Not Supported]\n");
        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpus[0]!["name"]!.GetValue<string>());
        Assert.Equal(37, gpus[0]!["utilizationPercent"]!.GetValue<double>());
        Assert.Equal(24564, gpus[0]!["memoryTotalMiB"]!.GetValue<double>());
        Assert.Null(gpus[1]!["utilizationPercent"]);
        Assert.Null(gpus[1]!["temperatureC"]);
    }

    [Theory]
    [InlineData("localhost", false, true)]
    [InlineData("127.0.0.1", false, true)]
    [InlineData("[::1]", false, true)]
    [InlineData("evil.example", false, false)]
    [InlineData("evil.example", true, true)]
    public void Dashboard_IsLocalHostName_BlocksRebindingUnlessRemoteAllowed(string host, bool allowRemote, bool expected)
    {
        Assert.Equal(expected, Dashboard.IsLocalHostName(host, allowRemote));
    }
}
