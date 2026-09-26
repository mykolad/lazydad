using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Core;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using Microsoft.Extensions.AI;
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
    public async Task CreateClient_WithAnApiKey_SendsTheKey()
    {
        var request = await SendOneRequestAsync(new LlmProviderOptions { Endpoint = "https://example.openai.azure.com/", ApiKey = "test-key" });

        Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task CreateClient_WithoutAnApiKey_SendsAnEntraIdToken()
    {
        var request = await SendOneRequestAsync(new LlmProviderOptions { Endpoint = "https://example.openai.azure.com/" });

        Assert.Equal("Bearer fake-entra-token", request.Headers.Authorization?.ToString());
        Assert.False(request.Headers.Contains("api-key"));
    }

    // Sends one chat request through the factory's client to a fake transport, and returns what went over the wire.
    private static async Task<HttpRequestMessage> SendOneRequestAsync(LlmProviderOptions options)
    {
        var handler = new CapturingHandler();
        var factory = new LlmClientFactory(
            Options.Create(new Dictionary<string, LlmProviderOptions> { ["AzureOpenAI"] = options }),
            new Lazy<TokenCredential>(() => new FakeCredential()),
            () => new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) });

        using var client = factory.CreateClient("AzureOpenAI", "gpt-6-luna");
        var response = await client.GetResponseAsync("Say OK.");

        Assert.Equal("OK", response.Text);
        return Assert.Single(handler.Requests);
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake-entra-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            const string completion = """
                {"id":"c1","object":"chat.completion","created":0,"model":"gpt-6-luna",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(completion, Encoding.UTF8, "application/json")
            });
        }
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
