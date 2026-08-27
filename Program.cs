using OllamaToLmStudioProxy;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

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
var configSection = builder.Configuration.GetSection("ProxySettings");

string lmStudioUrl = configSection["LmStudioUrl"]
    ?? builder.Configuration["LmStudioUrl"]
    ?? "http://localhost:1234";

string proxyHost = configSection["ProxyHost"]
    ?? builder.Configuration["ProxyHost"]
    ?? "0.0.0.0";

int proxyPort = configSection.GetValue<int?>("ProxyPort")
    ?? builder.Configuration.GetValue<int?>("ProxyPort")
    ?? 11434;

int timeoutSeconds = configSection.GetValue<int?>("TimeoutSeconds")
    ?? builder.Configuration.GetValue<int?>("TimeoutSeconds")
    ?? 600;

bool enableLogging = configSection.GetValue<bool?>("EnableLogging")
    ?? builder.Configuration.GetValue<bool?>("EnableLogging")
    ?? true;

string proxyUrl = $"http://{proxyHost}:{proxyPort}";

var app = builder.Build();

app.UseCors();

var http = new HttpClient
{
    BaseAddress = new Uri(lmStudioUrl),
    Timeout = TimeSpan.FromSeconds(timeoutSeconds)
};

Console.WriteLine("=================================================");
Console.WriteLine("  Ollama & OpenAI to LM Studio Proxy");
Console.WriteLine($"  Target LM Studio: {lmStudioUrl}");
Console.WriteLine($"  Listening on:     {proxyUrl}");
Console.WriteLine($"  Timeout:          {timeoutSeconds}s");
Console.WriteLine($"  Logging:          {(enableLogging ? "Enabled" : "Disabled")}");
Console.WriteLine("=================================================");

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
JsonObject? MatchModel(List<JsonObject> models, string requestedName)
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

    // 4. Default to first model if available
    return models.FirstOrDefault();
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

    // In Ollama, capabilities is an array of strings (e.g. ["completion", "chat", "tools"])
    var caps = new JsonArray();
    if (isEmbedding)
    {
        caps.Add("embedding");
    }
    else
    {
        caps.Add("completion");
        caps.Add("chat");
        if (isTools) caps.Add("tools");
        if (isVision) caps.Add("vision");
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
        ["model_info"] = new JsonObject
        {
            ["general.architecture"] = family,
            ["general.file_type"] = 2,
            ["general.parameter_count"] = 7_000_000_000L,
            ["general.quantization_version"] = 2
        },
        ["capabilities"] = caps.DeepClone(),
        ["modified_at"] = tagObj["modified_at"]?.ToString() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
    };
}

//
// ---------------------------------------------------------
//  Root Healthcheck & Version (Required by Copilot & Ollama tools)
// ---------------------------------------------------------
app.MapGet("/", () => Results.Text("Ollama is running"));
app.MapGet("/api/version", () => Results.Json(new JsonObject { ["version"] = "0.5.12" }));

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

    // Ollama streams by default unless stream is explicitly set to false
    bool stream = !reqJson.TryGetPropertyValue("stream", out var s) || s!.GetValue<bool>();

    var messages = reqJson["messages"]?.AsArray() ?? new JsonArray();

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["messages"] = messages.DeepClone(),
        ["stream"] = stream
    };

    // Forward tools if present
    if (reqJson.TryGetPropertyValue("tools", out var toolsNode) && toolsNode != null)
    {
        lmReq["tools"] = toolsNode.DeepClone();
    }

    // Forward options (temperature, top_p, max_tokens, etc.)
    if (reqJson.TryGetPropertyValue("options", out var optNode) && optNode is JsonObject opts)
    {
        if (opts.TryGetPropertyValue("temperature", out var temp)) lmReq["temperature"] = temp?.GetValue<double>();
        if (opts.TryGetPropertyValue("top_p", out var topP)) lmReq["top_p"] = topP?.GetValue<double>();
        if (opts.TryGetPropertyValue("num_predict", out var maxT)) lmReq["max_tokens"] = maxT?.GetValue<int>();
    }

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /api/chat forwarding to LM Studio (model: '{targetModel}', stream: {stream}, msgs: {messages.Count})");

    if (!stream)
    {
        // Non-streaming mode
        var resp = await http.PostAsync("/v1/chat/completions",
            new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));

        string respContent = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (enableLogging)
                Console.WriteLine($"[ERROR] LM Studio returned error: {resp.StatusCode} - {respContent}");
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await ctx.Response.WriteAsync(respContent);
            return;
        }

        var lmJson = JsonNode.Parse(respContent)?.AsObject();
        var choice = lmJson?["choices"]?[0];
        var msg = choice?["message"]?.AsObject() ?? new JsonObject { ["role"] = "assistant", ["content"] = "" };

        var outJson = new JsonObject
        {
            ["model"] = reqModel,
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["message"] = msg.DeepClone(),
            ["done"] = true,
            ["total_duration"] = 1_000_000_000L,
            ["load_duration"] = 10_000_000L,
            ["prompt_eval_count"] = lmJson?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0,
            ["eval_count"] = lmJson?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0
        };

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

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (!line.StartsWith("data:")) continue;

        string data = line.Substring(5).Trim();
        if (data == "[DONE]") break;

        try
        {
            var json = JsonNode.Parse(data)?.AsObject();
            if (json == null) continue;

            var choice = json["choices"]?[0];
            var delta = choice?["delta"]?.AsObject();
            if (delta == null) continue;

            string? chunk = delta["content"]?.ToString();
            var toolCalls = delta["tool_calls"];

            if (chunk != null || toolCalls != null)
            {
                var msgNode = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = chunk ?? ""
                };

                if (toolCalls != null)
                {
                    msgNode["tool_calls"] = toolCalls.DeepClone();
                }

                var outChunk = new JsonObject
                {
                    ["model"] = reqModel,
                    ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                    ["message"] = msgNode,
                    ["done"] = false
                };

                await ctx.Response.WriteAsync(outChunk.ToJsonString() + "\n");
                await ctx.Response.Body.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            LoggerUtility.LogMessage(enableLogging, $"[WARN] Error parsing stream chunk: {ex.Message}");
        }
    }

    // Send final closing chunk
    var doneChunk = new JsonObject
    {
        ["model"] = reqModel,
        ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = ""
        },
        ["done"] = true,
        ["total_duration"] = 1_000_000_000L,
        ["eval_count"] = 100
    };

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

    bool stream = !reqJson.TryGetPropertyValue("stream", out var s) || s!.GetValue<bool>();
    string prompt = reqJson["prompt"]?.ToString() ?? "";

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["prompt"] = prompt,
        ["stream"] = stream
    };

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /api/generate forwarding to LM Studio (model: '{targetModel}', stream: {stream})");

    if (!stream)
    {
        var resp = await http.PostAsync("/v1/completions",
            new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));

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
            ["response"] = lmJson?["choices"]?[0]?["text"]?.ToString() ?? "",
            ["done"] = true
        };

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
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (!line.StartsWith("data:")) continue;

        string data = line.Substring(5).Trim();
        if (data == "[DONE]") break;

        try
        {
            var json = JsonNode.Parse(data)?.AsObject();
            var chunk = json?["choices"]?[0]?["text"]?.ToString();

            if (chunk != null)
            {
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
        ["response"] = "",
        ["done"] = true
    };

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

    var input = reqJson["prompt"] ?? reqJson["input"];

    var lmReq = new JsonObject
    {
        ["model"] = targetModel,
        ["input"] = input?.DeepClone()
    };

    var resp = await http.PostAsync("/v1/embeddings",
        new StringContent(lmReq.ToJsonString(), Encoding.UTF8, "application/json"));

    string respContent = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        ctx.Response.StatusCode = (int)resp.StatusCode;
        await ctx.Response.WriteAsync(respContent);
        return;
    }

    var lmJson = JsonNode.Parse(respContent)?.AsObject();
    var data = lmJson?["data"]?.AsArray();

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

        await ctx.Response.WriteAsJsonAsync(new JsonObject
        {
            ["model"] = reqModel,
            ["embeddings"] = embeddingsArray
        });
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

//
// ---------------------------------------------------------
//  /v1/chat/completions & /chat/completions
// ---------------------------------------------------------
async Task HandleOpenAiChatCompletionsAsync(HttpContext ctx)
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

    // Update model name to match LM Studio model
    reqJson["model"] = targetModel;

    bool stream = reqJson.TryGetPropertyValue("stream", out var s) && s!.GetValue<bool>();

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /v1/chat/completions forwarding to LM Studio (model: '{targetModel}', stream: {stream})");

    var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent(reqJson.ToJsonString(), Encoding.UTF8, "application/json")
    };

    if (!stream)
    {
        var resp = await http.SendAsync(req);
        string respContent = await resp.Content.ReadAsStringAsync();
        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.WriteAsync(respContent);
        return;
    }

    // Streaming SSE mode (text/event-stream)
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    HttpResponseMessage streamResp;
    try
    {
        streamResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }
    catch (Exception ex)
    {
        LoggerUtility.LogMessage(enableLogging, $"[ERROR] Failed to send /v1/chat/completions to LM Studio: {ex.Message}");
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsync($"data: {{\"error\": \"{ex.Message}\"}}\n\n");
        return;
    }

    ctx.Response.StatusCode = (int)streamResp.StatusCode;

    using var streamContent = await streamResp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamContent, Encoding.UTF8);

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        await ctx.Response.WriteAsync(line + "\n");
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("data:"))
        {
            await ctx.Response.Body.FlushAsync();
        }
    }
    await ctx.Response.Body.FlushAsync();
}

app.MapPost("/v1/chat/completions", HandleOpenAiChatCompletionsAsync);
app.MapPost("/chat/completions", HandleOpenAiChatCompletionsAsync);

//
// ---------------------------------------------------------
//  /v1/completions & /completions
// ---------------------------------------------------------
async Task HandleOpenAiCompletionsAsync(HttpContext ctx)
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

    reqJson["model"] = targetModel;

    bool stream = reqJson.TryGetPropertyValue("stream", out var s) && s!.GetValue<bool>();

    LoggerUtility.LogMessage(enableLogging, $"[INFO] /v1/completions forwarding to LM Studio (model: '{targetModel}', stream: {stream})");

    var req = new HttpRequestMessage(HttpMethod.Post, "/v1/completions")
    {
        Content = new StringContent(reqJson.ToJsonString(), Encoding.UTF8, "application/json")
    };

    if (!stream)
    {
        var resp = await http.SendAsync(req);
        string respContent = await resp.Content.ReadAsStringAsync();
        ctx.Response.StatusCode = (int)resp.StatusCode;
        ctx.Response.Headers.ContentType = "application/json";
        await ctx.Response.WriteAsync(respContent);
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    var streamResp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    ctx.Response.StatusCode = (int)streamResp.StatusCode;

    using var streamContent = await streamResp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(streamContent, Encoding.UTF8);

    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        await ctx.Response.WriteAsync(line + "\n");
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("data:"))
        {
            await ctx.Response.Body.FlushAsync();
        }
    }
    await ctx.Response.Body.FlushAsync();
}

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
            ["owned_by"] = "ollama-lmstudio-proxy"
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
    var matched = MatchModel(lmModels, id);
    string modelId = matched?["key"]?.ToString() ?? matched?["id"]?.ToString() ?? id;

    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new JsonObject
    {
        ["id"] = modelId,
        ["object"] = "model",
        ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["owned_by"] = "ollama-lmstudio-proxy"
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
    reqJson["model"] = targetModel;

    var resp = await http.PostAsync("/v1/embeddings",
        new StringContent(reqJson.ToJsonString(), Encoding.UTF8, "application/json"));

    ctx.Response.StatusCode = (int)resp.StatusCode;
    ctx.Response.Headers.ContentType = "application/json";
    await ctx.Response.WriteAsync(await resp.Content.ReadAsStringAsync());
});

//
// ---------------------------------------------------------
//  RUN SERVER on configured Host and Port
// ---------------------------------------------------------
app.Run(proxyUrl);
