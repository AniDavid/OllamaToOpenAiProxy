using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy;

// Converts between Ollama's chat format and OpenAI's where the two differ.
internal static class OllamaTranslator
{
    // Ollama → OpenAI: tool-call arguments become JSON strings, every tool call gets an id,
    // and tool results get the tool_call_id that OpenAI backends require.
    internal static JsonArray ToOpenAiMessages(JsonArray messages)
    {
        var result = new JsonArray();
        var unanswered = new List<(string Id, string Name)>();
        int generatedIds = 0;

        foreach (var node in messages)
        {
            if (node is not JsonObject src)
            {
                result.Add(node?.DeepClone());
                continue;
            }

            var msg = src.DeepClone().AsObject();
            msg.Remove("tool_name");
            msg.Remove("thinking");
            string role = msg["role"]?.ToString() ?? "";

            if (msg["images"] is JsonArray images)
            {
                msg.Remove("images");
                var parts = ToContentParts(msg["content"]?.ToString() ?? "", images);
                if (parts.Any(p => p?["type"]?.ToString() == "image_url")) msg["content"] = parts;
            }

            if (role == "assistant" && msg["tool_calls"] is JsonArray calls)
            {
                unanswered.Clear();
                foreach (var call in calls.OfType<JsonObject>())
                {
                    string id = call["id"]?.ToString() is { Length: > 0 } existing ? existing : $"call_proxy_{generatedIds++}";
                    call["id"] = id;
                    call["type"] = "function";
                    if (call["function"] is JsonObject fn)
                    {
                        fn.Remove("index");
                        if (fn["arguments"] is not JsonValue args || args.GetValueKind() != JsonValueKind.String)
                            fn["arguments"] = fn["arguments"]?.ToJsonString() ?? "{}";
                        unanswered.Add((id, fn["name"]?.ToString() ?? ""));
                    }
                }
            }
            else if (role == "tool" && string.IsNullOrEmpty(msg["tool_call_id"]?.ToString()) && unanswered.Count > 0)
            {
                // Match by tool name when the client sends one, otherwise answer the calls in order.
                string? toolName = src["tool_name"]?.ToString() ?? src["name"]?.ToString();
                int i = Math.Max(0, unanswered.FindIndex(c => c.Name == toolName));
                msg["tool_call_id"] = unanswered[i].Id;
                unanswered.RemoveAt(i);
            }

            result.Add(msg);
        }

        return result;
    }

    // Ollama sends raw base64 images beside the text; OpenAI wants content parts with data URLs.
    static JsonArray ToContentParts(string text, JsonArray images)
    {
        var parts = new JsonArray();
        if (text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images)
        {
            string data = image?.ToString() ?? "";
            if (data.Length == 0) continue;
            string url = data.StartsWith("data:", StringComparison.Ordinal) ? data : $"data:{ImageMimeType(data)};base64,{data}";
            parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
        }
        return parts;
    }

    // Recognizes the format from the base64 of its first bytes; JPEG is the common default.
    static string ImageMimeType(string base64) =>
        base64.StartsWith("iVBOR", StringComparison.Ordinal) ? "image/png"
        : base64.StartsWith("R0lGOD", StringComparison.Ordinal) ? "image/gif"
        : base64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp"
        : "image/jpeg";

    static readonly (string Ollama, string OpenAi)[] OptionNames =
    [
        ("temperature", "temperature"), ("top_p", "top_p"), ("num_predict", "max_tokens"), ("stop", "stop"),
        ("seed", "seed"), ("top_k", "top_k"), ("min_p", "min_p"),
        ("presence_penalty", "presence_penalty"), ("frequency_penalty", "frequency_penalty"),
    ];

    // Sampling options only local backends (LM Studio, llama.cpp, vLLM) accept; OpenAI and Azure reject them.
    static readonly HashSet<string> NonStandardOptions = ["top_k", "min_p"];

    // OpenAI and Azure OpenAI answer 400 to request fields they don't know.
    internal static bool IsStrictOpenAi(string backendUrl) =>
        Uri.TryCreate(backendUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase));

    // Copies Ollama's options, format and think settings onto an OpenAI request.
    internal static void ApplyRequestOptions(JsonObject openAiRequest, JsonObject ollamaRequest, bool strictBackend = false)
    {
        if (ollamaRequest["options"] is JsonObject options)
        {
            foreach (var (ollamaName, openAiName) in OptionNames)
            {
                if (options[ollamaName] is not { } value) continue;
                if (strictBackend && NonStandardOptions.Contains(ollamaName)) continue;
                // Ollama uses -1/-2 for "no limit"/"fill context"; OpenAI only accepts a positive max_tokens.
                if (ollamaName == "num_predict" && !(value is JsonValue n && n.TryGetValue<int>(out int limit) && limit > 0)) continue;
                openAiRequest[openAiName] = value.DeepClone();
            }
        }

        switch (ollamaRequest["format"])
        {
            case JsonObject schema:
                openAiRequest["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = schema.DeepClone() }
                };
                break;
            // LM Studio rejects "json_object"; a permissive json_schema means the same and every backend accepts it.
            case JsonValue format when format.GetValueKind() == JsonValueKind.String && format.GetValue<string>() == "json":
                openAiRequest["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = new JsonObject { ["type"] = "object" } }
                };
                break;
        }

        // think: "low"/"medium"/"high" map to reasoning_effort; true/false only change what is returned.
        if (ollamaRequest["think"] is JsonValue think && think.GetValueKind() == JsonValueKind.String)
            openAiRequest["reasoning_effort"] = think.GetValue<string>();
    }

    // /api/generate → /v1/completions: the completions API has no system field, so the system prompt
    // goes in front of the prompt (Ollama ignores it for raw requests). suffix enables fill-in-the-middle.
    internal static void ApplyGenerateFields(JsonObject openAiRequest, JsonObject ollamaRequest)
    {
        string prompt = ollamaRequest["prompt"]?.ToString() ?? "";
        string system = ollamaRequest["system"]?.ToString() ?? "";
        bool raw = ollamaRequest["raw"] is JsonValue r && r.GetValueKind() == JsonValueKind.True;
        openAiRequest["prompt"] = raw || system.Length == 0 ? prompt : $"{system}\n\n{prompt}";

        if (ollamaRequest["suffix"]?.ToString() is { Length: > 0 } suffix)
            openAiRequest["suffix"] = suffix;
    }

    // LM Studio's params_string ("27B", "30B-A3B", "7.5B", "500M") → total parameter count; null when unrecognized.
    internal static long? ParameterCount(string? paramsString)
    {
        var match = System.Text.RegularExpressions.Regex.Match(paramsString ?? "", @"^\s*(\d+(?:\.\d+)?)\s*([BbMm])");
        if (!match.Success) return null;
        double value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (long)(value * (char.ToUpperInvariant(match.Groups[2].Value[0]) == 'B' ? 1e9 : 1e6));
    }

    // shortcut: think=false hides the reasoning but the backend still reasons; upgrade if backends get a portable off switch.
    internal static bool ThinkingDisabled(JsonObject ollamaRequest) =>
        ollamaRequest["think"] is JsonValue think && think.GetValueKind() == JsonValueKind.False;

    // Backends return reasoning as reasoning_content (LM Studio, vLLM, llama.cpp, DeepSeek) or reasoning (OpenRouter, Ollama).
    internal static string? ReasoningText(JsonNode? messageOrDelta) =>
        new[] { messageOrDelta?["reasoning_content"], messageOrDelta?["reasoning"] }
            .OfType<JsonValue>()
            .Where(v => v.GetValueKind() == JsonValueKind.String)
            .Select(v => v.GetValue<string>())
            .FirstOrDefault(s => s.Length > 0);

    // A backend's reasoning capability: true/false, or an object like {"allowed_options":["off","on"],"default":"off"}
    // that counts when reasoning can be switched on. Null when the backend doesn't say.
    internal static bool? ReportsReasoning(JsonNode? reasoning) => reasoning switch
    {
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        JsonObject o when o["allowed_options"] is JsonArray allowed => allowed.Any(a => a?.ToString() is { } s && s != "off"),
        JsonObject o when o["default"] is { } d => d.ToString() != "off",
        _ => null,
    };

    // OpenAI → Ollama: arguments become a JSON object instead of a string.
    internal static JsonArray ToOllamaToolCalls(JsonArray calls)
    {
        var result = new JsonArray();
        int index = 0;
        foreach (var call in calls.OfType<JsonObject>())
        {
            var fn = call["function"] as JsonObject;
            var converted = new JsonObject
            {
                ["function"] = new JsonObject
                {
                    ["index"] = index++,
                    ["name"] = fn?["name"]?.ToString() ?? "",
                    ["arguments"] = ParseArguments(fn?["arguments"])
                }
            };
            if (call["id"]?.ToString() is { Length: > 0 } id) converted["id"] = id;
            result.Add(converted);
        }
        return result;
    }

    static JsonNode? ParseArguments(JsonNode? args)
    {
        if (args is JsonObject obj) return obj.DeepClone();
        if (args is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return new JsonObject();

        string text = v.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? args.DeepClone();
        }
        catch (JsonException)
        {
            return args.DeepClone(); // Malformed model output: pass it through rather than lose it.
        }
    }

    // Marks an Ollama response as final: done_reason, real token counts, and the measured duration.
    // With metrics, it also fills Ollama's prompt/eval durations (from the first-token time) and hands the
    // token counts to the dashboard stats.
    internal static JsonObject AddDoneFields(JsonObject target, string? finishReason, JsonNode? usage, long startTimestamp, RequestMetrics? metrics = null)
    {
        long now = Stopwatch.GetTimestamp();
        target["done"] = true;
        target["done_reason"] = finishReason == "length" ? "length" : "stop"; // Ollama reports tool calls as "stop"
        target["total_duration"] = Nanoseconds(startTimestamp, now);
        if (usage?["prompt_tokens"] is JsonValue prompt) target["prompt_eval_count"] = prompt.GetValue<int>();
        if (usage?["completion_tokens"] is JsonValue completion) target["eval_count"] = completion.GetValue<int>();

        if (metrics != null)
        {
            metrics.PromptTokens = target["prompt_eval_count"]?.GetValue<int>();
            metrics.CompletionTokens = target["eval_count"]?.GetValue<int>();
            if (metrics.FirstTokenTimestamp is long first)
            {
                target["prompt_eval_duration"] = Nanoseconds(startTimestamp, first);
                target["eval_duration"] = Nanoseconds(first, now);
            }
        }
        // Clients expect the counts to exist; 0 when the backend sent no usage (the dashboard keeps "unknown").
        target["prompt_eval_count"] ??= 0;
        target["eval_count"] ??= 0;
        return target;
    }

    static long Nanoseconds(long from, long to) => Stopwatch.GetElapsedTime(from, to).Ticks * 100;
}

// Reads one OpenAI chat stream. Text passes straight through; tool-call fragments are collected
// and released whole, because Ollama clients expect each tool call complete with object arguments.
internal sealed class ChatStreamTranslator
{
    readonly SortedDictionary<int, (string? Id, string Name, StringBuilder Args)> _calls = new();
    readonly bool _includeThinking;

    public ChatStreamTranslator(bool includeThinking = true) => _includeThinking = includeThinking;

    public string? FinishReason { get; private set; }
    public JsonNode? Usage { get; private set; }

    // Returns the Ollama message to send for this chunk, or null when there is nothing to send yet.
    public JsonObject? Add(JsonObject chunk)
    {
        if (chunk["usage"] is JsonObject usage) Usage = usage.DeepClone();
        var choice = (chunk["choices"] as JsonArray)?.FirstOrDefault(); // the usage chunk has no choices
        if (choice?["finish_reason"]?.ToString() is { Length: > 0 } reason) FinishReason = reason;
        if (choice?["delta"] is not JsonObject delta) return null;

        if (delta["tool_calls"] is JsonArray parts)
        {
            foreach (var part in parts.OfType<JsonObject>())
            {
                // Without an index: a part with a name or id starts a new call, anything else continues the last one.
                bool startsCall = part["id"] != null || part["function"]?["name"] != null || _calls.Count == 0;
                int i = part["index"] is JsonValue idx && idx.TryGetValue<int>(out int index) ? index
                    : startsCall ? (_calls.Count == 0 ? 0 : _calls.Keys.Max() + 1)
                    : _calls.Keys.Max();
                var entry = _calls.TryGetValue(i, out var existing) ? existing : (null, "", new StringBuilder());
                entry.Id ??= part["id"]?.ToString();
                if (part["function"]?["name"]?.ToString() is { Length: > 0 } name) entry.Name = name;
                var args = part["function"]?["arguments"];
                entry.Args.Append(args is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : args?.ToJsonString());
                _calls[i] = entry;
            }
        }

        string? text = delta["content"]?.ToString();
        string? thinking = _includeThinking ? OllamaTranslator.ReasoningText(delta) : null;
        if (string.IsNullOrEmpty(text) && thinking == null) return null;

        var message = new JsonObject { ["role"] = "assistant", ["content"] = text ?? "" };
        if (thinking != null) message["thinking"] = thinking;
        return message;
    }

    // The collected tool calls in Ollama format, or null when the model made none.
    public JsonArray? TakeToolCalls()
    {
        if (_calls.Count == 0) return null;
        var openAiCalls = new JsonArray();
        foreach (var (_, call) in _calls)
        {
            var obj = new JsonObject { ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Args.ToString() } };
            if (call.Id != null) obj["id"] = call.Id;
            openAiCalls.Add(obj);
        }
        _calls.Clear();
        return OllamaTranslator.ToOllamaToolCalls(openAiCalls);
    }
}
