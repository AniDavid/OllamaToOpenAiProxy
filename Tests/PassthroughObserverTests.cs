using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy.Tests;

public class PassthroughObserverTests
{
    [Fact]
    public void RequestStreamUsage_AddsIncludeUsage_OnlyWhenClientDidNotAsk()
    {
        var plain = JsonNode.Parse("""{"stream":true}""")!.AsObject();
        Assert.True(PassthroughObserver.RequestStreamUsage(plain));
        Assert.True(plain["stream_options"]!["include_usage"]!.GetValue<bool>());

        var otherOptions = JsonNode.Parse("""{"stream_options":{"foo":1}}""")!.AsObject();
        Assert.True(PassthroughObserver.RequestStreamUsage(otherOptions));
        Assert.Equal(1, otherOptions["stream_options"]!["foo"]!.GetValue<int>());

        var asked = JsonNode.Parse("""{"stream_options":{"include_usage":true}}""")!.AsObject();
        Assert.False(PassthroughObserver.RequestStreamUsage(asked));
    }

    [Fact]
    public void StreamLines_MarkFirstToken_CaptureUsage_AndFlagUsageOnlyChunk()
    {
        var metrics = new RequestMetrics();
        Assert.Null(PassthroughObserver.ObserveStreamLine("""data: {"choices":[{"delta":{"role":"assistant"}}]}""", metrics).Error);
        Assert.Null(metrics.FirstTokenTimestamp);

        PassthroughObserver.ObserveStreamLine("""data: {"choices":[{"delta":{"content":"Hi"}}]}""", metrics);
        Assert.NotNull(metrics.FirstTokenTimestamp);

        var usage = PassthroughObserver.ObserveStreamLine("""data: {"choices":[],"usage":{"prompt_tokens":17,"completion_tokens":4}}""", metrics);
        Assert.True(usage.UsageOnly);
        Assert.Equal(17, metrics.PromptTokens);
        Assert.Equal(4, metrics.CompletionTokens);

        Assert.False(PassthroughObserver.ObserveStreamLine("data: [DONE]", metrics).UsageOnly);
        Assert.False(PassthroughObserver.ObserveStreamLine("", metrics).UsageOnly);
        Assert.Null(metrics.BackendError);
    }

    [Fact]
    public void StreamLine_WithError_IsReported_EvenInA200Stream()
    {
        // The shape LM Studio sends when the prompt is longer than the loaded context.
        var metrics = new RequestMetrics();
        var result = PassthroughObserver.ObserveStreamLine(
            """data: {"error":{"code":400,"message":"request (17108 tokens) exceeds the available context size (16384 tokens)","type":"exceed_context_size_error"}}""",
            metrics);

        Assert.Contains("exceeds the available context size", result.Error);
        Assert.Equal(result.Error, metrics.BackendError);

        var stats = new ProxyStats();
        var record = stats.Record("POST", "/v1/chat/completions", 200, metrics);
        Assert.Equal(record.Error, metrics.BackendError);
        Assert.Equal(1, stats.Snapshot()["errors"]!.GetValue<long>());
    }

    [Fact]
    public void Body_CapturesUsage_WithoutFirstToken_AndReportsErrors()
    {
        var metrics = new RequestMetrics();
        Assert.Null(PassthroughObserver.ObserveBody("""{"choices":[{"message":{"content":"x"}}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""", metrics));
        Assert.Equal(3, metrics.PromptTokens);
        Assert.Null(metrics.FirstTokenTimestamp);

        Assert.Equal("model not found", PassthroughObserver.ObserveBody("""{"error":"model not found"}""", new RequestMetrics()));
        Assert.Null(PassthroughObserver.ObserveBody("not json", new RequestMetrics()));
    }
}
