using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy.Tests;

public class OllamaTranslatorOptionsTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Apply(string ollamaRequest)
    {
        var openAi = new JsonObject();
        OllamaTranslator.ApplyRequestOptions(openAi, Obj(ollamaRequest));
        return openAi;
    }

    [Fact]
    public void Images_BecomeDataUrlContentParts()
    {
        var result = OllamaTranslator.ToOpenAiMessages(JsonNode.Parse("""
            [{"role":"user","content":"what is this?","images":["iVBORw0KGgo=","/9j/4AAQ"]}]
            """)!.AsArray());

        var parts = result[0]!["content"]!.AsArray();
        Assert.Equal("what is this?", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,/9j/4AAQ", parts[2]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Null(result[0]!["images"]);
    }

    [Fact]
    public void EmptyImages_LeaveTextContentAlone()
    {
        var result = OllamaTranslator.ToOpenAiMessages(JsonNode.Parse("""[{"role":"user","content":"hi","images":[]}]""")!.AsArray());
        Assert.Equal("hi", result[0]!["content"]!.GetValue<string>());
        Assert.Null(result[0]!["images"]);
    }

    [Fact]
    public void Options_MapToOpenAiNames_AndSkipUnlimitedNumPredict()
    {
        var req = Apply("""{"options":{"temperature":0.2,"num_predict":256,"stop":["</s>"],"seed":42,"top_k":40,"num_ctx":8192}}""");
        Assert.Equal(0.2, req["temperature"]!.GetValue<double>());
        Assert.Equal(256, req["max_tokens"]!.GetValue<int>());
        Assert.Equal("</s>", req["stop"]![0]!.GetValue<string>());
        Assert.Equal(42, req["seed"]!.GetValue<int>());
        Assert.Equal(40, req["top_k"]!.GetValue<int>());
        Assert.Null(req["num_ctx"]);

        Assert.Null(Apply("""{"options":{"num_predict":-1}}""")["max_tokens"]);
    }

    [Fact]
    public void Format_MapsToResponseFormat()
    {
        // LM Studio rejects json_object, so "json" becomes a schema that allows any object.
        var json = Apply("""{"format":"json"}""")["response_format"]!;
        Assert.Equal("json_schema", json["type"]!.GetValue<string>());
        Assert.Equal("object", json["json_schema"]!["schema"]!["type"]!.GetValue<string>());

        var schema = Apply("""{"format":{"type":"object","properties":{"a":{"type":"string"}}}}""")["response_format"]!;
        Assert.Equal("json_schema", schema["type"]!.GetValue<string>());
        Assert.Equal("object", schema["json_schema"]!["schema"]!["type"]!.GetValue<string>());
        Assert.NotNull(schema["json_schema"]!["name"]);
    }

    [Fact]
    public void Think_LevelMapsToReasoningEffort_BoolDoesNot()
    {
        Assert.Equal("high", Apply("""{"think":"high"}""")["reasoning_effort"]!.GetValue<string>());
        Assert.Null(Apply("""{"think":true}""")["reasoning_effort"]);
        Assert.True(OllamaTranslator.ThinkingDisabled(Obj("""{"think":false}""")));
        Assert.False(OllamaTranslator.ThinkingDisabled(Obj("""{"think":"low"}""")));
        Assert.False(OllamaTranslator.ThinkingDisabled(Obj("""{}""")));
    }

    private static JsonObject Generate(string ollamaRequest)
    {
        var openAi = new JsonObject();
        OllamaTranslator.ApplyGenerateFields(openAi, Obj(ollamaRequest));
        return openAi;
    }

    [Fact]
    public void Generate_PrependsSystem_UnlessRaw_AndForwardsSuffix()
    {
        Assert.Equal("Be brief.\n\nHi", Generate("""{"prompt":"Hi","system":"Be brief."}""")["prompt"]!.GetValue<string>());
        Assert.Equal("Hi", Generate("""{"prompt":"Hi","system":"Be brief.","raw":true}""")["prompt"]!.GetValue<string>());

        var fim = Generate("""{"prompt":"def add(a, b):\n    ","suffix":"\n\nprint(add(1, 2))"}""");
        Assert.Equal("def add(a, b):\n    ", fim["prompt"]!.GetValue<string>());
        Assert.Equal("\n\nprint(add(1, 2))", fim["suffix"]!.GetValue<string>());

        var plain = Generate("""{"prompt":"Hi","suffix":""}""");
        Assert.Equal("Hi", plain["prompt"]!.GetValue<string>());
        Assert.Null(plain["suffix"]);
    }

    [Theory]
    [InlineData("27B", 27_000_000_000L)]
    [InlineData("30B-A3B", 30_000_000_000L)]
    [InlineData("7.5B", 7_500_000_000L)]
    [InlineData("500M", 500_000_000L)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void ParameterCount_ParsesLmStudioSizes(string? input, long? expected)
    {
        Assert.Equal(expected, OllamaTranslator.ParameterCount(input));
    }

    [Fact]
    public void Reasoning_IsReadFromEitherFieldName()
    {
        Assert.Equal("a", OllamaTranslator.ReasoningText(Obj("""{"reasoning_content":"a"}""")));
        Assert.Equal("b", OllamaTranslator.ReasoningText(Obj("""{"reasoning":"b"}""")));
        Assert.Null(OllamaTranslator.ReasoningText(Obj("""{"content":"c"}""")));
    }

    [Fact]
    public void ChatStream_EmitsThinking_UnlessDisabled()
    {
        var on = new ChatStreamTranslator().Add(Obj("""{"choices":[{"delta":{"reasoning_content":"hmm"}}]}"""))!;
        Assert.Equal("hmm", on["thinking"]!.GetValue<string>());
        Assert.Equal("", on["content"]!.GetValue<string>());

        Assert.Null(new ChatStreamTranslator(includeThinking: false).Add(Obj("""{"choices":[{"delta":{"reasoning_content":"hmm"}}]}""")));
    }

    [Fact]
    public void StrictBackend_DropsLocalOnlySamplingOptions()
    {
        var req = new JsonObject();
        OllamaTranslator.ApplyRequestOptions(req, Obj("""{"options":{"top_k":40,"min_p":0.1,"temperature":0.5}}"""), strictBackend: true);
        Assert.Null(req["top_k"]);
        Assert.Null(req["min_p"]);
        Assert.Equal(0.5, req["temperature"]!.GetValue<double>());

        Assert.True(OllamaTranslator.IsStrictOpenAi("https://api.openai.com/v1"));
        Assert.True(OllamaTranslator.IsStrictOpenAi("https://myres.openai.azure.com"));
        Assert.False(OllamaTranslator.IsStrictOpenAi("http://localhost:1234"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("""{"allowed_options":["off","on"],"default":"off"}""", true)]
    [InlineData("""{"allowed_options":["off"]}""", false)]
    [InlineData("""{"default":"off"}""", false)]
    [InlineData("null", null)]
    public void ReportsReasoning_ReadsBoolOrOptions(string json, bool? expected)
    {
        Assert.Equal(expected, OllamaTranslator.ReportsReasoning(JsonNode.Parse(json)));
    }
}
