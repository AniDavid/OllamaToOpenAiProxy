using Microsoft.Extensions.Configuration;

namespace OllamaToOpenAiProxy.Tests;

public class ProxyConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void EnvName_BeatsProxySettingsAndRootKeys()
    {
        var config = Config(("OPENAI_URL", "http://env"), ("ProxySettings:OpenAiUrl", "http://section"), ("OpenAiUrl", "http://root"));
        Assert.Equal("http://env", ProxyConfig.Get(config, ["OPENAI_URL", "LMSTUDIO_URL"], "OpenAiUrl"));
    }

    [Fact]
    public void EnvAlias_IsUsedWhenPrimaryMissing()
    {
        var config = Config(("LMSTUDIO_URL", "http://alias"), ("ProxySettings:OpenAiUrl", "http://section"));
        Assert.Equal("http://alias", ProxyConfig.Get(config, ["OPENAI_URL", "LMSTUDIO_URL"], "OpenAiUrl"));
    }

    [Fact]
    public void FallsBackToSectionThenRoot_AndSkipsEmptyValues()
    {
        Assert.Equal("1", ProxyConfig.Get(Config(("PROXY_PORT", ""), ("ProxySettings:ProxyPort", "1"), ("ProxyPort", "2")), ["PROXY_PORT"], "ProxyPort"));
        Assert.Equal("2", ProxyConfig.Get(Config(("ProxyPort", "2")), ["PROXY_PORT"], "ProxyPort"));
        Assert.Null(ProxyConfig.Get(Config(), ["PROXY_PORT"], "ProxyPort"));
    }
}
