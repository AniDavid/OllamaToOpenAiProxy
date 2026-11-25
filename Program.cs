using OllamaToOpenAiProxy;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// winget starts the exe through a symlink in WinGet\Links, so resolve the real install folder.
string? linkTarget = Environment.ProcessPath is { } exePath ? File.ResolveLinkTarget(exePath, true)?.FullName : null;
string installDir = linkTarget is null ? AppContext.BaseDirectory : Path.GetDirectoryName(linkTarget)!;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = installDir });

// Config precedence (low → high): install-dir appsettings.json, %APPDATA%\OllamaToOpenAiProxy\appsettings.json
// (survives winget upgrades), then env vars and command-line args. The working directory is not read: started from
// another project's folder, the proxy would pick up that project's settings.
var configSources = builder.Configuration.Sources;
int afterFiles = configSources.ToList().FindLastIndex(s => s is Microsoft.Extensions.Configuration.FileConfigurationSource) + 1;
// No reload: settings are read once at startup, and a watcher would cover all of %APPDATA% when the folder is missing.
var userConfig = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OllamaToOpenAiProxy", "appsettings.json"),
    Optional = true,
    ReloadOnChange = false,
};
userConfig.ResolveFileProvider();
configSources.Insert(afterFiles, userConfig);

// Add CORS support
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

//
// ---------------------------------------------------------
//  Load Configuration from appsettings.json / ProxySettings
// ---------------------------------------------------------
var cfg = builder.Configuration;

string targetUrl = ProxyConfig.Get(cfg, ["OPENAI_URL", "LMSTUDIO_URL"], "OpenAiUrl", "BackendUrl", "TargetUrl", "LmStudioUrl")
    ?? "http://localhost:1234";
string proxyHost = ProxyConfig.Get(cfg, ["PROXY_HOST"], "ProxyHost") ?? "0.0.0.0";
int proxyPort = int.Parse(ProxyConfig.Get(cfg, ["PROXY_PORT"], "ProxyPort") ?? "11434");
int timeoutSeconds = int.Parse(ProxyConfig.Get(cfg, ["TIMEOUT_SECONDS"], "TimeoutSeconds") ?? "600");
bool enableLogging = bool.Parse(ProxyConfig.Get(cfg, ["ENABLE_LOGGING"], "EnableLogging") ?? "true");
bool enableDashboard = bool.Parse(ProxyConfig.Get(cfg, ["ENABLE_DASHBOARD"], "EnableDashboard") ?? "true");
bool dashboardAllowRemote = bool.Parse(ProxyConfig.Get(cfg, ["DASHBOARD_ALLOW_REMOTE"], "DashboardAllowRemote") ?? "false");
const string reportedOllamaVersion = "0.18.3"; // Minimum Ollama version the VS Code Copilot integration documents

string proxyUrl = $"http://{proxyHost}:{proxyPort}";
bool strictBackend = OllamaTranslator.IsStrictOpenAi(targetUrl);

var app = builder.Build();

app.UseCors();

var http = new HttpClient
{
    BaseAddress = new Uri(targetUrl),
    Timeout = TimeSpan.FromSeconds(timeoutSeconds)
};

Console.WriteLine("=================================================");
Console.WriteLine("  Ollama to OpenAI-Compatible Proxy");
Console.WriteLine($"  Target API:       {targetUrl}");
Console.WriteLine($"  Listening on:     {proxyUrl}");
Console.WriteLine($"  Timeout:          {timeoutSeconds}s");
Console.WriteLine($"  Logging:          {(enableLogging ? "Enabled" : "Disabled")}");
if (enableDashboard)
    Console.WriteLine($"  Dashboard:        http://localhost:{proxyPort}/dashboard{(dashboardAllowRemote ? " (open to the network)" : " (this machine only)")}");
Console.WriteLine("=================================================");

// Stats middleware: times every request for the dashboard (metadata only, never prompt text).
var stats = new ProxyStats();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/dashboard"))
    {
        await next();
        return;
    }

    var metrics = RequestMetrics.Attach(context);
    int status = 500;
    try
    {
        await next();
        status = context.Response.StatusCode;
    }
    finally
    {
        // Route pattern, not the raw path, so unknown URLs can't grow the per-endpoint table.
        string endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
        stats.Record(context.Request.Method, endpoint, status, metrics);
    }
});

// Logging Middleware
if (enableLogging)
{
    app.Use(async (context, next) =>
    {
        string method = context.Request.Method;
        string path = context.Request.Path;
        string query = context.Request.QueryString.Value ?? "";

        LoggerUtility.LogMessage(enableLogging, $"--> {method} {path}{query}");
        await next();
        LoggerUtility.LogMessage(enableLogging, $"<-- {method} {path} => {context.Response.StatusCode}");
    });
}

//
// ---------------------------------------------------------
//  Helper: Safely read and parse Request Body as JsonObject
// ---------------------------------------------------------
async Task<JsonObject?> ParseRequestBodyAsync(HttpContext ctx)
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        string text = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text)?.AsObject();
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[WARN] Failed to parse request body: {ex.Message}");
        return null;
    }
}

//
// ---------------------------------------------------------
//  Fetch Models from LM Studio (with fallback)
// ---------------------------------------------------------
async Task<List<JsonObject>> GetLmStudioModelsAsync()
{
    var list = new List<JsonObject>();

    // 1. Try LM Studio REST API: /api/v1/models
    try
    {
        var resp = await http.GetAsync("/api/v1/models");
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(content)?.AsObject();
            var modelsArray = json?["models"]?.AsArray() ?? json?["data"]?.AsArray();
            if (modelsArray != null)
            {
                foreach (var item in modelsArray)
                {
                    if (item is JsonObject obj)
                        list.Add(obj);
                }
                if (list.Count > 0) return list;
            }
        }
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[WARN] Could not fetch from /api/v1/models: {ex.Message}");
    }

    // 2. Fallback to OpenAI standard: /v1/models
    try
    {
        var resp = await http.GetAsync("/v1/models");
        if (resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(content)?.AsObject();
            var dataArray = json?["data"]?.AsArray();
            if (dataArray != null)
            {
                foreach (var item in dataArray)
                {
                    if (item is JsonObject obj)
                    {
                        string id = obj["id"]?.ToString() ?? "unknown";
                        list.Add(new JsonObject
                        {
                            ["key"] = id,
                            ["id"] = id,
                            ["type"] = "llm",
                            ["architecture"] = "llama",
                            ["params_string"] = "unknown",
                            ["format"] = "gguf"
                        });
                        // Keep the context size when the backend reports one (vLLM: max_model_len).
                        foreach (var field in new[] { "max_model_len", "context_length", "max_context_length" })
                            if (obj[field] is { } value) list[^1][field] = value.DeepClone();
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Could not fetch from /v1/models: {ex.Message}");
    }

    return list;
}

//
// ---------------------------------------------------------
//  Model Matching Helper
// ---------------------------------------------------------
JsonObject? MatchModel(List<JsonObject> models, string requestedName, bool allowFallback = true)
{
    if (models.Count == 0) return null;

    string cleanName = requestedName.Trim();
    string baseName = cleanName.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
        ? cleanName[..^7]
        : cleanName;

    // 1. Exact key/id match
    var match = models.FirstOrDefault(m =>
        string.Equals(m["key"]?.ToString(), cleanName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m["id"]?.ToString(), cleanName, StringComparison.OrdinalIgnoreCase));
    if (match != null) return match;

    // 2. Match without :latest
    match = models.FirstOrDefault(m =>
        string.Equals(m["key"]?.ToString(), baseName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m["id"]?.ToString(), baseName, StringComparison.OrdinalIgnoreCase));
    if (match != null) return match;

    // 3. Substring match
    match = models.FirstOrDefault(m =>
    {
        string key = m["key"]?.ToString() ?? m["id"]?.ToString() ?? "";
        return key.Contains(baseName, StringComparison.OrdinalIgnoreCase) ||
               baseName.Contains(key, StringComparison.OrdinalIgnoreCase);
    });
    if (match != null) return match;

    // 4. Default to first model if fallback is allowed
    return allowFallback ? models.FirstOrDefault() : null;
}

//
// ---------------------------------------------------------
//  Convert LM Studio metadata → Ollama metadata for /api/tags
// ---------------------------------------------------------
JsonObject ConvertLmStudioModelForTags(JsonObject m)
{
    string key = m["key"]?.ToString() ?? m["id"]?.ToString() ?? "unknown";
    string family = m["architecture"]?.ToString() ?? "llama";
    string sizeStr = m["params_string"]?.ToString() ?? "7B";
    string format = m["format"]?.ToString() ?? "gguf";
    string quant = m["quantization"]?["name"]?.ToString()
        ?? m["quantization"]?.ToString()
        ?? "Q4_K_M";
    long sizeBytes = m["size_bytes"]?.GetValue<long>() ?? 4_683_075_600L;

    bool isEmbedding = (m["type"]?.ToString() == "embedding") || key.Contains("embed", StringComparison.OrdinalIgnoreCase);
    bool isVision = (m["capabilities"]?["vision"]?.GetValue<bool>() == true) || key.Contains("vision", StringComparison.OrdinalIgnoreCase);
    bool isTools = (m["capabilities"]?["trained_for_tool_use"]?.GetValue<bool>() == true) || !isEmbedding;
    // shortcut: guesses reasoning models by name; upgrade once backends report a reasoning capability consistently.
    string[] reasoningHints = ["think", "reason", "qwen3", "deepseek-r1", "gpt-oss", "magistral", "qwq"];
    // Qwen3 Instruct/Coder variants (e.g. qwen3-coder-30b-a3b-instruct) don't reason.
    bool nonReasoningQwen = key.Contains("qwen3", StringComparison.OrdinalIgnoreCase)
        && (key.Contains("instruct", StringComparison.OrdinalIgnoreCase) || key.Contains("coder", StringComparison.OrdinalIgnoreCase));
    bool isThinking = !isEmbedding && (OllamaTranslator.ReportsReasoning(m["capabilities"]?["reasoning"])
        ?? (!nonReasoningQwen && reasoningHints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase))));

    // Only values real Ollama uses: completion, tools, insert, vision, embedding, thinking ("chat" is not one).
    var caps = new JsonArray();
    if (isEmbedding)
    {
        caps.Add("embedding");
    }
    else
    {
        caps.Add("completion");
        if (isTools) caps.Add("tools");
        if (isVision) caps.Add("vision");
        if (isThinking) caps.Add("thinking");
    }

    string digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    return new JsonObject
    {
        ["name"] = key,
        ["model"] = key,
        ["modified_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        ["size"] = sizeBytes,
        ["digest"] = digest,
        ["details"] = new JsonObject
        {
            ["parent_model"] = "",
            ["format"] = format,
            ["family"] = family,
            ["families"] = new JsonArray { family },
            ["parameter_size"] = sizeStr,
            ["quantization_level"] = quant
        },
        ["capabilities"] = caps
    };
}

//
// ---------------------------------------------------------
//  Convert LM Studio metadata → Ollama metadata for /api/show
// ---------------------------------------------------------
JsonObject ConvertLmStudioModelForShow(JsonObject m)
{
    var tagObj = ConvertLmStudioModelForTags(m);
    string key = tagObj["name"]!.ToString();
    var details = tagObj["details"]!.AsObject();
    var caps = tagObj["capabilities"]!.AsArray();
    string family = details["family"]?.ToString() ?? "llama";

    string template = "{{ if .System }}<|im_start|>system\n{{ .System }}<|im_end|>\n{{ end }}{{ if .Prompt }}<|im_start|>user\n{{ .Prompt }}<|im_end|>\n{{ end }}<|im_start|>assistant\n{{ .Response }}<|im_end|>";
    string modelfile = $"# Modelfile for {key}\nFROM {key}\nTEMPLATE \"\"\"{template}\"\"\"\nPARAMETER stop \"<|im_start|>\"\nPARAMETER stop \"<|im_end|>\"\n";

    // Real Ollama reports "<architecture>.context_length"; Copilot reads it for the model's context window.
    var modelInfo = new JsonObject
    {
        ["general.architecture"] = family,
        ["general.file_type"] = 2,
        ["general.parameter_count"] = OllamaTranslator.ParameterCount(m["params_string"]?.ToString()) ?? 7_000_000_000L,
        ["general.quantization_version"] = 2
    };
    if (RequestMetrics.ModelContextLength(m) is int contextLength)
        modelInfo[$"{family}.context_length"] = contextLength;

    return new JsonObject
    {
        ["license"] = "Unknown",
        ["modelfile"] = modelfile,
        ["parameters"] = "stop \"<|im_start|>\"\nstop \"<|im_end|>\"",
        ["template"] = template,
        ["system"] = "",
        ["details"] = new JsonObject
        {
            ["parent_model"] = "",
            ["format"] = details["format"]?.ToString() ?? "gguf",
            ["family"] = family,
            ["families"] = new JsonArray { family },
            ["parameter_size"] = details["parameter_size"]?.ToString() ?? "7B",
            ["quantization_level"] = details["quantization_level"]?.ToString() ?? "Q4_K_M"
        },
        ["model_info"] = modelInfo,
        ["capabilities"] = caps.DeepClone(),
        ["modified_at"] = tagObj["modified_at"]?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
    };
}

//
// ---------------------------------------------------------
//  Root Healthcheck & Version (Required by Copilot & Ollama tools)
// ---------------------------------------------------------
app.MapGet("/", () => Results.Text("Ollama is running"));
app.MapGet("/api/version", () => Results.Json(new JsonObject { ["version"] = reportedOllamaVersion }));

//
// ---------------------------------------------------------
//  /api/tags — List models (Ollama format)
// ---------------------------------------------------------
async Task HandleTagsAsync(HttpContext ctx)
{
    var lmModels = await GetLmStudioModelsAsync();
    var ollamaModels = new JsonArray();

    foreach (var m in lmModels)
    {
        ollamaModels.Add(ConvertLmStudioModelForTags(m));
    }

    LoggerUtility.LogMessage(enableLogging, $"Returning {ollamaModels.Count} models to client.");
    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new JsonObject { ["models"] = ollamaModels });
}

app.MapGet("/api/tags", HandleTagsAsync);
app.MapPost("/api/tags", HandleTagsAsync);

//
// ---------------------------------------------------------
//  /api/show — Model metadata (Visual Studio Copilot calls POST)
// ---------------------------------------------------------
async Task HandleShowAsync(HttpContext ctx)
{
    string? modelName = null;

    // Check query string
    if (ctx.Request.Query.TryGetValue("name", out var qName))
        modelName = qName.ToString();
    else if (ctx.Request.Query.TryGetValue("model", out var qModel))
        modelName = qModel.ToString();

    // Check JSON body
    if (string.IsNullOrWhiteSpace(modelName))
    {
        var body = await ParseRequestBodyAsync(ctx);
        modelName = body?["name"]?.ToString() ?? body?["model"]?.ToString();
    }

    if (string.IsNullOrWhiteSpace(modelName))
    {
        LoggerUtility.LogMessage(enableLogging, "[WARN] /api/show called without model name.");
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = "Missing model name." });
        return;
    }

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /api/show requested for: '{modelName}'");

    var lmModels = await GetLmStudioModelsAsync();
    var match = MatchModel(lmModels, modelName);

    ctx.Response.Headers.ContentType = "application/json";

    if (match != null)
    {
        var showObj = ConvertLmStudioModelForShow(match);
        await ctx.Response.WriteAsJsonAsync(showObj);
        return;
    }

    LoggerUtility.LogMessage(enableLogging, $"[WARN] Model '{modelName}' not found in LM Studio. Returning generic metadata.");
    var fallback = new JsonObject
    {
        ["key"] = modelName,
        ["architecture"] = "llama",
        ["params_string"] = "7B",
        ["format"] = "gguf"
    };
    await ctx.Response.WriteAsJsonAsync(ConvertLmStudioModelForShow(fallback));
}

app.MapGet("/api/show", HandleShowAsync);
app.MapPost("/api/show", HandleShowAsync);

//
// ---------------------------------------------------------
//  /api/ps — Running models
// ---------------------------------------------------------
async Task HandlePsAsync(HttpContext ctx)
{
    var lmModels = await GetLmStudioModelsAsync();
    var activeModels = new JsonArray();
    foreach (var m in lmModels)
    {
        activeModels.Add(ConvertLmStudioModelForTags(m));
    }
    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new JsonObject { ["models"] = activeModels });
}

app.MapGet("/api/ps", HandlePsAsync);
app.MapPost("/api/ps", HandlePsAsync);

//
// ---------------------------------------------------------
//  /api/pull — Fake pull progress
// ---------------------------------------------------------
app.MapPost("/api/pull", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "application/x-ndjson";

    var step1 = new JsonObject { ["status"] = "pulling manifest" };
    await ctx.Response.WriteAsync(step1.ToJsonString() + "\n");
    await ctx.Response.Body.FlushAsync();
    await Task.Delay(100);

    var step2 = new JsonObject { ["status"] = "verifying sha256 digest" };
    await ctx.Response.WriteAsync(step2.ToJsonString() + "\n");
    await ctx.Response.Body.FlushAsync();
    await Task.Delay(100);

    var step3 = new JsonObject { ["status"] = "success" };
    await ctx.Response.WriteAsync(step3.ToJsonString() + "\n");
    await ctx.Response.Body.FlushAsync();
});

//
// ---------------------------------------------------------
//  /api/chat — Ollama native chat completions (NDJSON)
// ---------------------------------------------------------
app.MapPost("/api/chat", async (HttpContext ctx) =>
{
    var reqJson = await ParseRequestBodyAsync(ctx);
    if (reqJson == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = "Invalid JSON body" });
        return;
    }

    string reqModel = reqJson["model"]?.ToString() ?? "default";
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, reqModel);
    string targetModel = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? reqModel;
    RequestMetrics.For(ctx)?.SetModel(targetModel, matched);

    // Ollama streams by default unless stream is explicitly set to false
    bool stream = !reqJson.TryGetPropertyValue("stream", out var s) || s!.GetValue<bool>();

    var messages = reqJson["messages"]?.AsArray() ?? new JsonArray();
    long started = System.Diagnostics.Stopwatch.GetTimestamp();

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["messages"] = OllamaTranslator.ToOpenAiMessages(messages),
        ["stream"] = stream
    };
    if (stream) lmReq["stream_options"] = new JsonObject { ["include_usage"] = true };

    // Forward tools if present
    if (reqJson.TryGetPropertyValue("tools", out var toolsNode) && toolsNode != null)
    {
        lmReq["tools"] = toolsNode.DeepClone();
    }

    OllamaTranslator.ApplyRequestOptions(lmReq, reqJson, strictBackend);
    bool includeThinking = !OllamaTranslator.ThinkingDisabled(reqJson);

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /api/chat forwarding to LM Studio (model: '{targetModel}', stream: {stream}, msgs: {messages.Count})");

    if (!stream)
    {
        // Non-streaming mode
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync("/v1/chat/completions",
                new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        catch (Exception ex)
        {
            LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send /api/chat to backend: {ex.Message}");
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
            return;
        }

        string respContent = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (enableLogging)
                Console.WriteLine($"[ERROR] Backend returned error: {resp.StatusCode} - {respContent}");
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await ctx.Response.WriteAsync(respContent);
            return;
        }

        var lmJson = JsonNode.Parse(respContent)?.AsObject();
        var choice = lmJson?["choices"]?[0];
        var msg = choice?["message"];

        var outMsg = new JsonObject { ["role"] = "assistant", ["content"] = msg?["content"]?.ToString() ?? "" };
        if (msg?["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0)
            outMsg["tool_calls"] = OllamaTranslator.ToOllamaToolCalls(toolCalls);
        if (includeThinking && OllamaTranslator.ReasoningText(msg) is { } thinking)
            outMsg["thinking"] = thinking;

        var outJson = new JsonObject
        {
            ["model"] = reqModel,
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["message"] = outMsg
        };
        OllamaTranslator.AddDoneFields(outJson, choice?["finish_reason"]?.ToString(), lmJson?["usage"], started, RequestMetrics.For(ctx));

        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(outJson);
        return;
    }

    // Streaming mode (NDJSON)
    ctx.Response.Headers.ContentType = "application/x-ndjson";

    var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json")
    };

    HttpResponseMessage streamResp;
    try
    {
        streamResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send request to LM Studio: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync(new JsonObject { ["error"] = ex.Message }.ToJsonString() + "\n");
        return;
    }

    if (!streamResp.IsSuccessStatusCode)
    {
        var errStr = await streamResp.Content.ReadAsStringAsync();
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] LM Studio stream error: {streamResp.StatusCode} - {errStr}");
        ctx.Response.StatusCode = (int)streamResp.StatusCode;
        await ctx.Response.WriteAsync(errStr);
        return;
    }

    using var streamContent = await streamResp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamContent, Encoding.UTF8);

    var translator = new ChatStreamTranslator(includeThinking);

    async Task WriteChunkAsync(JsonObject message)
    {
        RequestMetrics.For(ctx)?.MarkFirstToken();
        var outChunk = new JsonObject
        {
            ["model"] = reqModel,
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["message"] = message,
            ["done"] = false
        };
        await ctx.Response.WriteAsync(outChunk.ToJsonString() + "\n");
        await ctx.Response.Body.FlushAsync();
    }

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (!line.StartsWith("data:")) continue;

        string data = line.Substring(5).Trim();
        if (data == "[DONE]") break;

        try
        {
            if (JsonNode.Parse(data) is JsonObject json && translator.Add(json) is { } message)
                await WriteChunkAsync(message);
        }
        catch (Exception ex)
        {
            LoggerUtility.LogMessage(enableLogging, $"[WARN] Error parsing stream chunk: {ex.Message}");
        }
    }

    if (translator.TakeToolCalls() is { } streamedToolCalls)
        await WriteChunkAsync(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = streamedToolCalls });

    // Send final closing chunk
    var doneChunk = new JsonObject
    {
        ["model"] = reqModel,
        ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = ""
        }
    };
    OllamaTranslator.AddDoneFields(doneChunk, translator.FinishReason, translator.Usage, started, RequestMetrics.For(ctx));

    await ctx.Response.WriteAsync(doneChunk.ToJsonString() + "\n");
    await ctx.Response.Body.FlushAsync();
});

//
// ---------------------------------------------------------
//  /api/generate — Ollama text completions (NDJSON)
// ---------------------------------------------------------
app.MapPost("/api/generate", async (HttpContext ctx) =>
{
    var reqJson = await ParseRequestBodyAsync(ctx);
    if (reqJson == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = "Invalid JSON body" });
        return;
    }

    string reqModel = reqJson["model"]?.ToString() ?? "default";
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, reqModel);
    string targetModel = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? reqModel;
    RequestMetrics.For(ctx)?.SetModel(targetModel, matched);

    bool stream = !reqJson.TryGetPropertyValue("stream", out var s) || s!.GetValue<bool>();

    long started = System.Diagnostics.Stopwatch.GetTimestamp();

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["stream"] = stream
    };
    OllamaTranslator.ApplyGenerateFields(lmReq, reqJson);
    OllamaTranslator.ApplyRequestOptions(lmReq, reqJson, strictBackend);
    if (stream) lmReq["stream_options"] = new JsonObject { ["include_usage"] = true };

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /api/generate forwarding to LM Studio (model: '{targetModel}', stream: {stream})");

    if (!stream)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync("/v1/completions",
                new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        catch (Exception ex)
        {
            LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send /api/generate to backend: {ex.Message}");
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
            return;
        }

        string respContent = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await ctx.Response.WriteAsync(respContent);
            return;
        }

        var lmJson = JsonNode.Parse(respContent)?.AsObject();
        var outJson = new JsonObject
        {
            ["model"] = reqModel,
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["response"] = lmJson?["choices"]?[0]?["text"]?.ToString() ?? ""
        };
        OllamaTranslator.AddDoneFields(outJson, lmJson?["choices"]?[0]?["finish_reason"]?.ToString(), lmJson?["usage"], started, RequestMetrics.For(ctx));

        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(outJson);
        return;
    }

    // Streaming mode (NDJSON)
    ctx.Response.Headers.ContentType = "application/x-ndjson";

    var req = new HttpRequestMessage(HttpMethod.Post, "/v1/completions")
    {
        Content = new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json")
    };

    HttpResponseMessage streamResp;
    try
    {
        streamResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync(new JsonObject { ["error"] = ex.Message }.ToJsonString() + "\n");
        return;
    }

    using var streamContent = await streamResp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamContent, Encoding.UTF8);

    string? line;
    string? finishReason = null;
    JsonNode? usage = null;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (!line.StartsWith("data:")) continue;

        string data = line.Substring(5).Trim();
        if (data == "[DONE]") break;

        try
        {
            var json = JsonNode.Parse(data)?.AsObject();
            if (json?["usage"] is JsonObject u) usage = u.DeepClone();
            var firstChoice = (json?["choices"] as JsonArray)?.FirstOrDefault(); // the usage chunk has no choices
            if (firstChoice?["finish_reason"]?.ToString() is { Length: > 0 } fr) finishReason = fr;
            var chunk = firstChoice?["text"]?.ToString();

            if (!string.IsNullOrEmpty(chunk))
            {
                RequestMetrics.For(ctx)?.MarkFirstToken();
                var outChunk = new JsonObject
                {
                    ["model"] = reqModel,
                    ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                    ["response"] = chunk,
                    ["done"] = false
                };

                await ctx.Response.WriteAsync(outChunk.ToJsonString() + "\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch { }
    }

    var doneChunk = new JsonObject
    {
        ["model"] = reqModel,
        ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        ["response"] = ""
    };
    OllamaTranslator.AddDoneFields(doneChunk, finishReason, usage, started, RequestMetrics.For(ctx));

    await ctx.Response.WriteAsync(doneChunk.ToJsonString() + "\n");
    await ctx.Response.Body.FlushAsync();
});

//
// ---------------------------------------------------------
//  /api/embeddings and /api/embed — Text Embeddings
// ---------------------------------------------------------
async Task HandleEmbeddingsAsync(HttpContext ctx)
{
    var reqJson = await ParseRequestBodyAsync(ctx);
    if (reqJson == null)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    string reqModel = reqJson["model"]?.ToString() ?? "default";
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, reqModel);
    string targetModel = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? reqModel;
    RequestMetrics.For(ctx)?.SetModel(targetModel, matched);

    var input = reqJson["prompt"] ?? reqJson["input"];

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["input"] = input?.DeepClone()
    };
    if (reqJson["dimensions"] is JsonValue dimensions) lmReq["dimensions"] = dimensions.DeepClone();

    HttpResponseMessage resp;
    try
    {
        resp = await http.PostAsync("/v1/embeddings",
            new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send /api/embeddings to backend: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
        return;
    }

    string respContent = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        ctx.Response.StatusCode = (int)resp.StatusCode;
        await ctx.Response.WriteAsync(respContent);
        return;
    }

    var lmJson = JsonNode.Parse(respContent)?.AsObject();
    var data = lmJson?["data"]?.AsArray();
    if (RequestMetrics.For(ctx) is { } embedMetrics && lmJson?["usage"]?["prompt_tokens"] is JsonValue embedTokens)
        embedMetrics.PromptTokens = embedTokens.GetValue<int>();

    if (ctx.Request.Path.Value?.EndsWith("/embed", StringComparison.OrdinalIgnoreCase) == true)
    {
        var embeddingsArray = new JsonArray();
        if (data != null)
        {
            foreach (var item in data)
            {
                if (item?["embedding"] is JsonArray emb)
                    embeddingsArray.Add(emb.DeepClone());
            }
        }

        var embedResponse = new JsonObject
        {
            ["model"] = reqModel,
            ["embeddings"] = embeddingsArray
        };
        if (lmJson?["usage"]?["prompt_tokens"] is JsonValue promptTokens)
            embedResponse["prompt_eval_count"] = promptTokens.DeepClone();
        await ctx.Response.WriteAsJsonAsync(embedResponse);
    }
    else
    {
        var firstEmb = data?[0]?["embedding"]?.AsArray() ?? new JsonArray();
        await ctx.Response.WriteAsJsonAsync(new JsonObject
        {
            ["embedding"] = firstEmb.DeepClone()
        });
    }
}

app.MapPost("/api/embeddings", HandleEmbeddingsAsync);
app.MapPost("/api/embed", HandleEmbeddingsAsync);

//
// =========================================================
//  OPENAI-COMPATIBLE ENDPOINTS (/v1/chat/completions, etc.)
//  Used by Visual Studio Copilot Chat (Microsoft.Extensions.AI / OpenAI.Chat.ChatClient)
// =========================================================

// Passes an OpenAI-format request to the backend unchanged (apart from the model name) and records
// tokens, first-token time and backend errors for the dashboard.
async Task ForwardOpenAiRequestAsync(HttpContext ctx, string backendPath)
{
    var reqJson = await ParseRequestBodyAsync(ctx);
    if (reqJson == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = "Invalid JSON body" });
        return;
    }

    string reqModel = reqJson["model"]?.ToString() ?? "default";
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, reqModel);
    string targetModel = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? reqModel;
    var metrics = RequestMetrics.For(ctx);
    metrics?.SetModel(targetModel, matched);

    // Update model name to match the backend's model
    reqJson["model"] = targetModel;

    bool stream = reqJson.TryGetPropertyValue("stream", out var s) && s!.GetValue<bool>();
    bool hideUsageChunk = stream && PassthroughObserver.RequestStreamUsage(reqJson);

    LoggerUtility.LogMessage(enableLogging, $"[INFO] {backendPath} forwarding to backend (model: '{targetModel}', stream: {stream})");

    var req = new HttpRequestMessage(HttpMethod.Post, backendPath)
    {
        Content = new StringContent(reqJson.ToJsonString(), Encoding.UTF8, "application/json")
    };

    static string Shorten(string text) => text.Length <= 500 ? text : text[..500] + "…";

    if (!stream)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req);
        }
        catch (Exception ex)
        {
            LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send {backendPath} to backend: {ex.Message}");
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
            return;
        }

        string respContent = await resp.Content.ReadAsStringAsync();
        string? backendError = PassthroughObserver.ObserveBody(respContent, metrics);
        if (backendError != null || !resp.IsSuccessStatusCode)
            LoggerUtility.LogMessage(enableLogging, $"[ERROR] Backend returned {(int)resp.StatusCode} for {backendPath}: {Shorten(backendError ?? respContent)}");

        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.WriteAsync(respContent);
        return;
    }

    // Streaming SSE mode (text/event-stream)
    HttpResponseMessage streamResp;
    try
    {
        streamResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send {backendPath} to backend: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
        return;
    }

    if (!streamResp.IsSuccessStatusCode)
    {
        string errorBody = await streamResp.Content.ReadAsStringAsync();
        string? backendError = PassthroughObserver.ObserveBody(errorBody, metrics);
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Backend returned {(int)streamResp.StatusCode} for {backendPath}: {Shorten(backendError ?? errorBody)}");
        ctx.Response.StatusCode = (int)streamResp.StatusCode;
        ctx.Response.Headers.ContentType = streamResp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await ctx.Response.WriteAsync(errorBody);
        return;
    }

    ctx.Response.StatusCode = (int)streamResp.StatusCode;
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    using var streamContent = await streamResp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamContent, Encoding.UTF8);

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        var observed = PassthroughObserver.ObserveStreamLine(line, metrics);
        if (observed.Error != null)
            LoggerUtility.LogMessage(enableLogging, $"[ERROR] Backend error inside {backendPath} stream: {Shorten(observed.Error)}");
        if (hideUsageChunk && observed.UsageOnly) continue; // the client didn't ask for usage; we did

        await ctx.Response.WriteAsync(line + "\n");
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("data:"))
        {
            await ctx.Response.Body.FlushAsync();
        }
    }
    await ctx.Response.Body.FlushAsync();
}

//
// ---------------------------------------------------------
//  /v1/chat/completions & /chat/completions
// ---------------------------------------------------------
Task HandleOpenAiChatCompletionsAsync(HttpContext ctx) => ForwardOpenAiRequestAsync(ctx, "/v1/chat/completions");

app.MapPost("/v1/chat/completions", HandleOpenAiChatCompletionsAsync);
app.MapPost("/chat/completions", HandleOpenAiChatCompletionsAsync);

//
// ---------------------------------------------------------
//  /v1/completions & /completions
// ---------------------------------------------------------
Task HandleOpenAiCompletionsAsync(HttpContext ctx) => ForwardOpenAiRequestAsync(ctx, "/v1/completions");

app.MapPost("/v1/completions", HandleOpenAiCompletionsAsync);
app.MapPost("/completions", HandleOpenAiCompletionsAsync);

//
// ---------------------------------------------------------
//  /v1/models & /models (OpenAI format)
// ---------------------------------------------------------
async Task HandleOpenAiModelsAsync(HttpContext ctx)
{
    var lmModels = await GetLmStudioModelsAsync();
    var dataArray = new JsonArray();

    foreach (var m in lmModels)
    {
        string id = m["key"]?.ToString() ?? m["id"]?.ToString() ?? "unknown";
        dataArray.Add(new JsonObject
        {
            ["id"] = id,
            ["object"] = "model",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["owned_by"] = "ollama-openai-proxy"
        });
    }

    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new JsonObject
    {
        ["object"] = "list",
        ["data"] = dataArray
    });
}

app.MapGet("/v1/models", HandleOpenAiModelsAsync);
app.MapGet("/models", HandleOpenAiModelsAsync);

//
// ---------------------------------------------------------
//  /v1/models/{id} (OpenAI format)
// ---------------------------------------------------------
app.MapGet("/v1/models/{**id}", async (HttpContext ctx, string id) =>
{
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, id, allowFallback: false);
    string modelId = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? id;

    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new JsonObject
    {
        ["id"] = modelId,
        ["object"] = "model",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["owned_by"] = "ollama-openai-proxy"
    });
});

//
// ---------------------------------------------------------
//  /v1/embeddings (OpenAI format)
// ---------------------------------------------------------
app.MapPost("/v1/embeddings", async (HttpContext ctx) =>
{
    var reqJson = await ParseRequestBodyAsync(ctx);
    if (reqJson == null)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    string reqModel = reqJson["model"]?.ToString() ?? "default";
    var lmModels = await GetLmStudioModelsAsync();
    var matched = MatchModel(lmModels, reqModel);
    string targetModel = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? reqModel;
    RequestMetrics.For(ctx)?.SetModel(targetModel, matched);
    reqJson["model"] = targetModel;

    HttpResponseMessage resp;
    try
    {
        resp = await http.PostAsync("/v1/embeddings",
            new StringContent(reqJson.ToJsonString(), Encoding.UTF8, "application/json"));
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send /v1/embeddings to backend: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new JsonObject { ["error"] = ex.Message });
        return;
    }

    ctx.Response.StatusCode = (int)resp.StatusCode;
    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsync(await resp.Content.ReadAsStringAsync());
});

//
// ---------------------------------------------------------
//  Dashboard (/dashboard)
// ---------------------------------------------------------
if (enableDashboard)
{
    Dashboard.Map(app, stats, http, GetLmStudioModelsAsync, ConvertLmStudioModelForTags, new JsonObject
    {
        ["targetUrl"] = Dashboard.RedactUrl(targetUrl),
        ["listen"] = proxyUrl,
        ["timeoutSeconds"] = timeoutSeconds,
        ["logging"] = enableLogging,
        ["dashboardAllowRemote"] = dashboardAllowRemote,
        ["reportedOllamaVersion"] = reportedOllamaVersion,
    }, dashboardAllowRemote);
}

//
// ---------------------------------------------------------
//  RUN SERVER on configured Host and Port
// ---------------------------------------------------------
app.Run(proxyUrl);

public partial class Program { }
