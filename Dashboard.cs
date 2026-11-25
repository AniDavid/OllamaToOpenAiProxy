using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;

namespace OllamaToOpenAiProxy;

internal static class Dashboard
{
    // Loopback always; anyone else only when DASHBOARD_ALLOW_REMOTE is on.
    internal static bool IsAllowed(IPAddress? remote, bool allowRemote)
    {
        if (allowRemote) return true;
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return IPAddress.IsLoopback(remote);
    }

    // Blocks DNS rebinding: a website whose name resolves to 127.0.0.1 still sends its own name as Host.
    internal static bool IsLocalHostName(string host, bool allowRemote) =>
        allowRemote || host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    static bool IsAllowed(HttpContext ctx, bool allowRemote) =>
        IsAllowed(ctx.Connection.RemoteIpAddress, allowRemote) && IsLocalHostName(ctx.Request.Host.Host, allowRemote);

    // Hides user:password if someone put credentials in the backend URL.
    internal static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length == 0) return url;
        return new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
    }

    public static void Map(
        WebApplication app,
        ProxyStats stats,
        HttpClient backend,
        Func<Task<List<JsonObject>>> getModels,
        Func<JsonObject, JsonObject> toOllamaModel,
        JsonObject config,
        bool allowRemote)
    {
        // Everything below runs only when the page asks, and is cached so an open page adds little load.
        var models = new Cached<List<JsonObject>>(TimeSpan.FromSeconds(1), getModels); // 1 s so "Loaded" follows LM Studio closely
        var probe = new Cached<JsonObject>(TimeSpan.FromSeconds(1), () => ProbeAsync(backend));
        var gpu = new Cached<JsonArray?>(TimeSpan.FromSeconds(1), () => Task.Run(GpuStats.Read));

        string version = typeof(Dashboard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown";
        config["proxyVersion"] = version;

        app.MapGet("/dashboard", (HttpContext ctx) =>
        {
            if (!IsAllowed(ctx, allowRemote)) return Results.NotFound();
            using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("OllamaToOpenAiProxy.dashboard.html")!;
            using var reader = new StreamReader(stream);
            return Results.Content(reader.ReadToEnd(), "text/html; charset=utf-8");
        }).WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute()); // the Ollama API allows any origin; the dashboard must not be readable by other websites

        app.MapGet("/dashboard/api/stats", async (HttpContext ctx) =>
        {
            if (!IsAllowed(ctx, allowRemote)) return Results.NotFound();

            var modelList = new JsonArray();
            foreach (var m in await models.GetAsync())
            {
                var converted = toOllamaModel(m);
                modelList.Add(new JsonObject
                {
                    ["name"] = converted["name"]?.DeepClone(),
                    // Read from the backend's own data: the Ollama conversion fills gaps with placeholders like "7B".
                    ["parameterSize"] = Reported(m["params_string"]),
                    ["quantization"] = Reported((m["quantization"] as JsonObject)?["name"] ?? m["quantization"] as JsonValue),
                    ["capabilities"] = converted["capabilities"]?.DeepClone(),
                    ["contextLength"] = RequestMetrics.ModelContextLength(m),
                    // LM Studio lists loaded copies in loaded_instances; null when the backend doesn't say.
                    ["loaded"] = m["loaded_instances"] is JsonArray instances ? instances.Count > 0 : null,
                });
            }

            return Results.Json(new JsonObject
            {
                ["config"] = config.DeepClone(),
                ["backend"] = (await probe.GetAsync()).DeepClone(),
                ["models"] = modelList,
                ["stats"] = stats.Snapshot(),
                ["gpu"] = (await gpu.GetAsync())?.DeepClone(),
            });
        }).WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute());
    }

    static string? Reported(JsonNode? value) =>
        value?.ToString() is { Length: > 0 } s && s != "unknown" ? s : null;

    static async Task<JsonObject> ProbeAsync(HttpClient backend)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await backend.GetAsync("/v1/models", timeout.Token);
            return new JsonObject
            {
                ["reachable"] = true,
                ["status"] = (int)resp.StatusCode,
                ["latencyMs"] = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
            };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["reachable"] = false, ["error"] = ex.Message };
        }
    }

    // Shares one in-flight or recent result between callers; a failed load is retried on the next call.
    sealed class Cached<T>(TimeSpan ttl, Func<Task<T>> load)
    {
        readonly object _lock = new();
        Task<T>? _task;
        DateTime _loadedAt;

        public Task<T> GetAsync()
        {
            lock (_lock)
            {
                if (_task == null || _task.IsFaulted || DateTime.UtcNow - _loadedAt > ttl)
                {
                    _loadedAt = DateTime.UtcNow;
                    _task = load();
                }
                return _task;
            }
        }
    }
}
