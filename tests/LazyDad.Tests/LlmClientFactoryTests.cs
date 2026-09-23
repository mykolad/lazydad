using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public class LlmClientFactoryTests
{
    private static LlmClientFactory CreateFactory(Dictionary<string, LlmProviderOptions> providers)
        => new(Options.Create(providers));

    [Fact]
    public void CreateClient_ForAzureOpenAI_ReturnsChatClient()
    {
        // Constructing the client makes no network call, so a placeholder endpoint is fine.
        var factory = CreateFactory(new()
        {
            ["AzureOpenAI"] = new() { Endpoint = "https://example.openai.azure.com/", ApiKey = "test-key" }
        });

        using var client = factory.CreateClient("AzureOpenAI", "gpt-5.3-chat");

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateClient_ForUnconfiguredProvider_Throws()
    {
        var factory = CreateFactory([]);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient("AzureOpenAI", "gpt-5.3-chat"));
        Assert.Contains("'AzureOpenAI' is not configured", ex.Message);
    }

    [Fact]
    public void CreateClient_ForConfiguredButUnsupportedProvider_Throws()
    {
        var factory = CreateFactory(new() { ["SomethingElse"] = new() { Endpoint = "https://example.com/" } });

        var ex = Assert.Throws<NotSupportedException>(() => factory.CreateClient("SomethingElse", "model"));
        Assert.Contains("'SomethingElse' is not supported", ex.Message);
    }
}
