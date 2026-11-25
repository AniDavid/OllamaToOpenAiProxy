namespace OllamaToOpenAiProxy;

internal static class ProxyConfig
{
    // First non-empty value wins: env-style names (OPENAI_URL), then ProxySettings:<key>, then root <key>.
    internal static string? Get(IConfiguration config, string[] envNames, params string[] keys) =>
        envNames.Select(n => config[n])
            .Concat(keys.Select(k => config[$"ProxySettings:{k}"]))
            .Concat(keys.Select(k => config[k]))
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
