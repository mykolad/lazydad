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
    public void CreateClient_WithoutAnApiKey_UsesEntraId()
    {
        // DefaultAzureCredential only fetches a token on the first request, so this makes no network call either.
        var options = new LlmProviderOptions { Endpoint = "https://example.openai.azure.com/" };
        var factory = CreateFactory(new() { ["AzureOpenAI"] = options });

        using var client = factory.CreateClient("AzureOpenAI", "gpt-6-luna");

        Assert.NotNull(client);
        Assert.True(LlmClientFactory.UsesEntraId(options));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("a-key", false)]
    public void UsesEntraId_OnlyWhenNoApiKeyIsConfigured(string apiKey, bool expected)
        => Assert.Equal(expected, LlmClientFactory.UsesEntraId(new LlmProviderOptions { ApiKey = apiKey }));

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
