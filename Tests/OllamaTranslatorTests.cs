using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy.Tests;

public class OllamaTranslatorTests
{
    private static JsonArray Arr(string json) => JsonNode.Parse(json)!.AsArray();
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void ToOpenAiMessages_StringifiesArguments_AddsIds_AndLinksToolResults()
    {
        var result = OllamaTranslator.ToOpenAiMessages(Arr("""
        [
          {"role":"user","content":"weather?"},
          {"role":"assistant","content":"","tool_calls":[
            {"function":{"index":0,"name":"get_weather","arguments":{"city":"Paris"}}},
            {"function":{"index":1,"name":"get_time","arguments":{"tz":"CET"}}}]},
          {"role":"tool","tool_name":"get_time","content":"12:00"},
          {"role":"tool","content":"sunny"}
        ]
        """));

        var calls = result[1]!["tool_calls"]!.AsArray();
        Assert.Equal("""{"city":"Paris"}""", calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("function", calls[0]!["type"]!.GetValue<string>());
        Assert.Null(calls[0]!["function"]!["index"]);
        string weatherId = calls[0]!["id"]!.GetValue<string>();
        string timeId = calls[1]!["id"]!.GetValue<string>();
        Assert.NotEqual(weatherId, timeId);

        // Matched by tool_name first, then the remaining call in order; tool_name is not forwarded.
        Assert.Equal(timeId, result[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Null(result[2]!["tool_name"]);
        Assert.Equal(weatherId, result[3]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void ToOpenAiMessages_KeepsExistingIdsAndStringArguments()
    {
        var result = OllamaTranslator.ToOpenAiMessages(Arr("""
        [
          {"role":"assistant","tool_calls":[{"id":"call_abc","function":{"name":"f","arguments":"{\"a\":1}"}}]},
          {"role":"tool","tool_call_id":"call_abc","content":"ok"}
        ]
        """));

        Assert.Equal("call_abc", result[0]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("""{"a":1}""", result[0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("call_abc", result[1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public void ToOllamaToolCalls_ParsesArgumentsIntoObjects_AndKeepsMalformedText()
    {
        var result = OllamaTranslator.ToOllamaToolCalls(Arr("""
        [
          {"id":"call_1","type":"function","function":{"name":"f","arguments":"{\"city\":\"Paris\"}"}},
          {"type":"function","function":{"name":"g","arguments":"{not json"}}
        ]
        """));

        Assert.Equal("Paris", result[0]!["function"]!["arguments"]!["city"]!.GetValue<string>());
        Assert.Equal("call_1", result[0]!["id"]!.GetValue<string>());
        Assert.Equal(1, result[1]!["function"]!["index"]!.GetValue<int>());
        Assert.Equal("{not json", result[1]!["function"]!["arguments"]!.GetValue<string>());
    }

    [Fact]
    public void ChatStream_AssemblesFragmentedToolCalls_AndCapturesUsage()
    {
        var t = new ChatStreamTranslator();
        Assert.Equal("Hi", t.Add(Obj("""{"choices":[{"delta":{"content":"Hi"}}]}"""))!["content"]!.GetValue<string>());
        Assert.Null(t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_weather","arguments":"{\"ci"}}]}}]}""")));
        Assert.Null(t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"ty\":\"Paris\"}"}}]}}]}""")));
        t.Add(Obj("""{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));
        t.Add(Obj("""{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":7}}"""));

        var calls = t.TakeToolCalls()!;
        Assert.Single(calls);
        Assert.Equal("get_weather", calls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", calls[0]!["function"]!["arguments"]!["city"]!.GetValue<string>());
        Assert.Equal("call_1", calls[0]!["id"]!.GetValue<string>());
        Assert.Null(t.TakeToolCalls());

        var done = OllamaTranslator.AddDoneFields(new JsonObject(), t.FinishReason, t.Usage, System.Diagnostics.Stopwatch.GetTimestamp());
        Assert.Equal("stop", done["done_reason"]!.GetValue<string>());
        Assert.Equal(12, done["prompt_eval_count"]!.GetValue<int>());
        Assert.Equal(7, done["eval_count"]!.GetValue<int>());
        Assert.True(done["done"]!.GetValue<bool>());
    }

    [Fact]
    public void ChatStream_WholeToolCallsWithoutIndex_StaySeparate()
    {
        var t = new ChatStreamTranslator();
        t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"function":{"name":"a","arguments":"{}"}},{"function":{"name":"b","arguments":"{\"x\":1}"}}]}}]}"""));

        var calls = t.TakeToolCalls()!;
        Assert.Equal(2, calls.Count);
        Assert.Equal("b", calls[1]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal(1, calls[1]!["function"]!["arguments"]!["x"]!.GetValue<int>());
    }

    [Fact]
    public void AddDoneFields_ReportsLength_AndZeroCountsWithoutUsage()
    {
        var metrics = new RequestMetrics();
        var done = OllamaTranslator.AddDoneFields(new JsonObject(), "length", null, System.Diagnostics.Stopwatch.GetTimestamp(), metrics);
        Assert.Equal("length", done["done_reason"]!.GetValue<string>());
        Assert.Equal(0, done["eval_count"]!.GetValue<int>());
        Assert.Equal(0, done["prompt_eval_count"]!.GetValue<int>());
        Assert.Null(metrics.CompletionTokens); // the dashboard shows "unknown", not 0
        Assert.True(done["total_duration"]!.GetValue<long>() >= 0);
    }

    [Fact]
    public void ChatStream_FragmentsWithoutIndex_JoinTheCallTheyContinue()
    {
        var t = new ChatStreamTranslator();
        t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"id":"a","function":{"name":"f","arguments":"{\"x\""}}]}}]}"""));
        t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"function":{"arguments":":1}"}}]}}]}"""));
        t.Add(Obj("""{"choices":[{"delta":{"tool_calls":[{"index":"bad","id":"b","function":{"name":"g","arguments":"{}"}}]}}]}"""));

        var calls = t.TakeToolCalls()!;
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, calls[0]!["function"]!["arguments"]!["x"]!.GetValue<int>());
        Assert.Equal("g", calls[1]!["function"]!["name"]!.GetValue<string>());
    }
}
