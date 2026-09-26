using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Text;
using Azure.AI.OpenAI;
using Azure.Core;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
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
        using var factory = CreateFactory(new()
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

    [Fact]
    public async Task CreateClient_SharesOneClientPerModel_ThatOutlivesTheCallersDispose()
    {
        var handler = new CapturingHandler();
        using var factory = CreateFactory(handler, new LlmProviderOptions { Endpoint = "https://example.openai.azure.com/", ApiKey = "test-key" });

        var first = factory.CreateClient("AzureOpenAI", "gpt-6-luna");
        var shared = first.GetService<OpenTelemetryChatClient>();
        // Callers dispose their client after each call (using var).
        first.Dispose();
        using var second = factory.CreateClient("AzureOpenAI", "gpt-6-luna");
        using var otherModel = factory.CreateClient("AzureOpenAI", "Kimi-K2.5");

        Assert.NotNull(shared);
        Assert.Same(shared, second.GetService<OpenTelemetryChatClient>());
        Assert.NotSame(shared, otherModel.GetService<OpenTelemetryChatClient>());
        Assert.Equal("OK", (await second.GetResponseAsync("Say OK.")).Text);
    }

    [Fact]
    public async Task CreateClient_TracesEachCall_WithoutThePromptOrTheResponse()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LazyDadTelemetry.ChatClientName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);

        await SendOneRequestAsync(new LlmProviderOptions { Endpoint = "https://example.openai.azure.com/", ApiKey = "test-key" });

        Activity span;
        lock (spans) span = Assert.Single(spans);
        Assert.Equal("gpt-6-luna", span.GetTagItem("gen_ai.request.model"));
        // No message content: neither as attributes nor as events.
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value is string text && text.Contains("Say OK."));
        Assert.DoesNotContain(span.TagObjects, tag => tag.Key.Contains("messages"));
        Assert.Empty(span.Events);
    }

    private static LlmClientFactory CreateFactory(CapturingHandler handler, LlmProviderOptions options)
        => new(
            Options.Create(new Dictionary<string, LlmProviderOptions> { ["AzureOpenAI"] = options }),
            new Lazy<TokenCredential>(() => new FakeCredential()),
            () => new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) });

    // Sends one chat request through the factory's client to a fake transport, and returns what went over the wire.
    private static async Task<HttpRequestMessage> SendOneRequestAsync(LlmProviderOptions options)
    {
        var handler = new CapturingHandler();
        using var factory = CreateFactory(handler, options);

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
        using var factory = CreateFactory([]);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient("AzureOpenAI", "gpt-5.3-chat"));
        Assert.Contains("'AzureOpenAI' is not configured", ex.Message);
    }

    [Fact]
    public void CreateClient_ForConfiguredButUnsupportedProvider_Throws()
    {
        using var factory = CreateFactory(new() { ["SomethingElse"] = new() { Endpoint = "https://example.com/" } });

        var ex = Assert.Throws<NotSupportedException>(() => factory.CreateClient("SomethingElse", "model"));
        Assert.Contains("'SomethingElse' is not supported", ex.Message);
    }
}
