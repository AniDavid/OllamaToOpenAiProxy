using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy;

// What a handler learned about one request (model, tokens, first-token time). The stats middleware
// attaches it to HttpContext.Items and records it once the response is finished.
internal sealed class RequestMetrics
{
    const string ItemsKey = "proxy.metrics";

    public long StartTimestamp { get; } = Stopwatch.GetTimestamp();
    public long? FirstTokenTimestamp { get; private set; }
    public string? Model { get; private set; }
    public int? ContextLength { get; private set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    // Set when the backend reports an error, including one sent inside a stream that started with 200.
    public string? BackendError { get; set; }

    public static RequestMetrics Attach(HttpContext ctx)
    {
        var metrics = new RequestMetrics();
        ctx.Items[ItemsKey] = metrics;
        return metrics;
    }

    public static RequestMetrics? For(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemsKey, out var value) ? value as RequestMetrics : null;

    public void MarkFirstToken() => FirstTokenTimestamp ??= Stopwatch.GetTimestamp();

    public void SetModel(string name, JsonObject? backendModel)
    {
        Model = name;
        ContextLength = ModelContextLength(backendModel);
    }

    // Backends name this differently: LM Studio (loaded/max_context_length, loaded_instances[].config),
    // vLLM (max_model_len), others (context_length). Loaded size first, since that is what is in use.
    internal static int? ModelContextLength(JsonObject? model) =>
        new[]
        {
            (model?["loaded_instances"] as JsonArray)?.FirstOrDefault()?["config"]?["context_length"],
            model?["loaded_context_length"], model?["max_context_length"], model?["context_length"], model?["max_model_len"],
        }
        .OfType<JsonValue>()
        .Select(v => v.TryGetValue<int>(out int n) ? n : (int?)null)
        .FirstOrDefault(n => n > 0);
}

internal sealed record RequestRecord(
    DateTime Time, string Method, string Endpoint, string? Model, int Status, double TotalMs,
    double? FirstTokenMs, int? PromptTokens, int? CompletionTokens, double? TokensPerSecond, int? ContextLength, string? Error);

// Watches an OpenAI-format response that the proxy passes through unchanged, for the dashboard:
// token usage, time of the first token, and errors the backend sends inside a 200 stream.
internal static class PassthroughObserver
{
    public readonly record struct LineResult(string? Error, bool UsageOnly);

    // Asks the backend to report token usage at the end of a stream. Returns true when the client
    // didn't ask for it, so the extra usage-only chunk can be held back from the client.
    public static bool RequestStreamUsage(JsonObject request)
    {
        if (request["stream_options"]?["include_usage"] is JsonValue v && v.GetValueKind() == JsonValueKind.True) return false;
        if (request["stream_options"] is JsonObject options) options["include_usage"] = true;
        else request["stream_options"] = new JsonObject { ["include_usage"] = true };
        return true;
    }

    // One line of an SSE stream ("data: {...}").
    public static LineResult ObserveStreamLine(string line, RequestMetrics? metrics)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return default;
        string data = line[5..].Trim();
        if (data.Length == 0 || data == "[DONE]") return default;

        JsonObject? json;
        try { json = JsonNode.Parse(data) as JsonObject; }
        catch (JsonException) { return default; }
        if (json == null) return default;

        string? error = Observe(json, metrics, streaming: true);
        bool usageOnly = json["usage"] is JsonObject && json["choices"] is JsonArray { Count: 0 };
        return new LineResult(error, usageOnly);
    }

    // A whole non-streaming response body. Returns the backend's error message, if any.
    public static string? ObserveBody(string body, RequestMetrics? metrics)
    {
        try { return JsonNode.Parse(body) is JsonObject json ? Observe(json, metrics, streaming: false) : null; }
        catch (JsonException) { return null; }
    }

    static string? Observe(JsonObject json, RequestMetrics? metrics, bool streaming)
    {
        if (metrics != null && json["usage"] is JsonObject usage)
        {
            if (usage["prompt_tokens"] is JsonValue p && p.TryGetValue<int>(out int prompt)) metrics.PromptTokens = prompt;
            if (usage["completion_tokens"] is JsonValue c && c.TryGetValue<int>(out int completion)) metrics.CompletionTokens = completion;
        }

        if (metrics != null && streaming)
        {
            var choice = (json["choices"] as JsonArray)?.FirstOrDefault();
            var delta = choice?["delta"];
            if (HasText(delta?["content"]) || HasText(delta?["reasoning_content"]) || HasText(delta?["reasoning"])
                || delta?["tool_calls"] is JsonArray || HasText(choice?["text"]))
                metrics.MarkFirstToken();
        }

        string? error = json["error"] switch
        {
            JsonObject e => e["message"]?.ToString() ?? e.ToJsonString(),
            JsonValue v => v.ToString(),
            _ => null,
        };
        if (error != null && metrics != null) metrics.BackendError = error;
        return error;
    }

    static bool HasText(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Length > 0;
}

// In-memory traffic stats since start: the last requests plus running totals. Nothing is persisted,
// and no prompt or response text is ever kept.
internal sealed class ProxyStats
{
    public const int Capacity = 200;

    readonly object _lock = new();
    readonly Queue<RequestRecord> _recent = new();
    readonly Dictionary<string, (long Count, long Errors)> _byEndpoint = new();
    long _requests, _errors, _promptTokens, _completionTokens, _tpsSamples;
    double _tpsSum;

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    // endpoint is the matched route pattern, so the set of keys stays small.
    public RequestRecord Record(string method, string endpoint, int status, RequestMetrics metrics, long? endTimestamp = null)
    {
        long end = endTimestamp ?? Stopwatch.GetTimestamp();
        double totalMs = Stopwatch.GetElapsedTime(metrics.StartTimestamp, end).TotalMilliseconds;
        double? firstTokenMs = metrics.FirstTokenTimestamp is long first
            ? Stopwatch.GetElapsedTime(metrics.StartTimestamp, first).TotalMilliseconds
            : null;

        // Same definition as Ollama's eval_count / eval_duration: generated tokens over the time after the first token.
        double? tps = null;
        if (metrics.FirstTokenTimestamp is long firstToken && metrics.CompletionTokens is > 0)
        {
            double seconds = Stopwatch.GetElapsedTime(firstToken, end).TotalSeconds;
            if (seconds >= 0.001) tps = Math.Round(metrics.CompletionTokens.Value / seconds, 1);
        }

        var record = new RequestRecord(DateTime.UtcNow, method, endpoint, metrics.Model, status, Math.Round(totalMs, 1),
            firstTokenMs is null ? null : Math.Round(firstTokenMs.Value, 1), metrics.PromptTokens, metrics.CompletionTokens, tps, metrics.ContextLength,
            metrics.BackendError);
        bool isError = status >= 400 || metrics.BackendError != null;

        lock (_lock)
        {
            _recent.Enqueue(record);
            while (_recent.Count > Capacity) _recent.Dequeue();

            _requests++;
            if (isError) _errors++;
            _promptTokens += metrics.PromptTokens ?? 0;
            _completionTokens += metrics.CompletionTokens ?? 0;
            if (tps is double t) { _tpsSum += t; _tpsSamples++; }

            var (count, errors) = _byEndpoint.GetValueOrDefault(endpoint);
            _byEndpoint[endpoint] = (count + 1, errors + (isError ? 1 : 0));
        }

        return record;
    }

    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            var endpoints = new JsonArray();
            foreach (var (endpoint, (count, errors)) in _byEndpoint.OrderByDescending(e => e.Value.Count))
                endpoints.Add(new JsonObject { ["endpoint"] = endpoint, ["count"] = count, ["errors"] = errors });

            var recent = new JsonArray();
            foreach (var r in _recent.Reverse())
            {
                recent.Add(new JsonObject
                {
                    ["time"] = r.Time.ToString("o"),
                    ["method"] = r.Method,
                    ["endpoint"] = r.Endpoint,
                    ["model"] = r.Model,
                    ["status"] = r.Status,
                    ["totalMs"] = r.TotalMs,
                    ["firstTokenMs"] = r.FirstTokenMs,
                    ["promptTokens"] = r.PromptTokens,
                    ["completionTokens"] = r.CompletionTokens,
                    ["tokensPerSecond"] = r.TokensPerSecond,
                    ["contextLength"] = r.ContextLength,
                    ["error"] = r.Error,
                });
            }

            return new JsonObject
            {
                ["startedAt"] = StartedAt.ToString("o"),
                ["uptimeSeconds"] = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
                ["requests"] = _requests,
                ["errors"] = _errors,
                ["promptTokens"] = _promptTokens,
                ["completionTokens"] = _completionTokens,
                ["avgTokensPerSecond"] = _tpsSamples == 0 ? null : Math.Round(_tpsSum / _tpsSamples, 1),
                ["endpoints"] = endpoints,
                ["recent"] = recent,
            };
        }
    }
}
