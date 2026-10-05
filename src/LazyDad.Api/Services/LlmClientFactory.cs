using System.Collections.Concurrent;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using LazyDad.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

/// <summary>
/// One chat client per provider and model, shared for the app's lifetime: the Azure OpenAI client is meant to be
/// reused, and its telemetry wrapper owns a meter, which would restart the LLM metrics if it were created per call.
/// Callers may dispose what <see cref="CreateClient"/> returns (a no-op); the factory disposes the shared clients.
/// </summary>
public sealed class LlmClientFactory : ILlmClientFactory, IDisposable
{
    private readonly IOptions<Dictionary<string, LlmProviderOptions>> providers;
    private readonly ConcurrentDictionary<(string Provider, string Model), Lazy<IChatClient>> clients = new();
    private readonly ConcurrentDictionary<(string Provider, string Deployment), Lazy<IEmbeddingGenerator<string, Embedding<float>>>> embeddingGenerators = new();
    // Entra ID when no API key is configured: the app's managed identity in Azure, the developer's
    // `az login` locally. Created on first use; one per factory (a singleton), so its token cache is shared.
    private readonly Lazy<TokenCredential> entraCredential;
    private readonly Func<AzureOpenAIClientOptions> clientOptions;

    public LlmClientFactory(IOptions<Dictionary<string, LlmProviderOptions>> providers)
        : this(providers, new Lazy<TokenCredential>(() => new DefaultAzureCredential()), () => new AzureOpenAIClientOptions())
    {
    }

    /// <summary>For tests: a fake credential, and client options with a fake transport.</summary>
    internal LlmClientFactory(
        IOptions<Dictionary<string, LlmProviderOptions>> providers,
        Lazy<TokenCredential> entraCredential,
        Func<AzureOpenAIClientOptions> clientOptions)
    {
        this.providers = providers;
        this.entraCredential = entraCredential;
        this.clientOptions = clientOptions;
    }

    public IChatClient CreateClient(string providerName, string modelName)
    {
        if (!providers.Value.TryGetValue(providerName, out var options))
            throw new InvalidOperationException($"LLM provider '{providerName}' is not configured.");

        // GetOrAdd may run its factory more than once for concurrent first callers; only the Lazy it stores
        // gets created, so exactly one client per model exists.
        var key = (providerName, modelName);
        var client = clients.GetOrAdd(key, _ => new Lazy<IChatClient>(() => providerName switch
        {
            "AzureOpenAI" => CreateAzureOpenAIClient(options, modelName),
            _ => throw new NotSupportedException($"LLM provider '{providerName}' is not supported.")
        }));
        try
        {
            return new SharedChatClient(client.Value);
        }
        catch
        {
            // Lazy would keep the exception for good; drop it, so the next call tries again.
            clients.TryRemove(new KeyValuePair<(string, string), Lazy<IChatClient>>(key, client));
            throw;
        }
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string providerName, string deploymentName)
    {
        if (!providers.Value.TryGetValue(providerName, out var options))
            throw new InvalidOperationException($"LLM provider '{providerName}' is not configured.");

        var key = (providerName, deploymentName);
        var generator = embeddingGenerators.GetOrAdd(key, _ => new Lazy<IEmbeddingGenerator<string, Embedding<float>>>(() => providerName switch
        {
            "AzureOpenAI" => AzureOpenAIClient(options).GetEmbeddingClient(deploymentName).AsIEmbeddingGenerator()
                .AsBuilder()
                .UseOpenTelemetry(configure: telemetry => telemetry.EnableSensitiveData = false)
                .Build(),
            _ => throw new NotSupportedException($"LLM provider '{providerName}' is not supported.")
        }));
        try
        {
            return new SharedEmbeddingGenerator(generator.Value);
        }
        catch
        {
            embeddingGenerators.TryRemove(new KeyValuePair<(string, string), Lazy<IEmbeddingGenerator<string, Embedding<float>>>>(key, generator));
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var client in clients.Values.Where(c => c.IsValueCreated))
            client.Value.Dispose();
        clients.Clear();
        foreach (var generator in embeddingGenerators.Values.Where(g => g.IsValueCreated))
            generator.Value.Dispose();
        embeddingGenerators.Clear();
    }

    /// <summary>
    /// The API key while one is configured, else Entra ID. Removing the key setting switches an
    /// environment over (issue #10) without a code change.
    /// </summary>
    internal static bool UsesEntraId(LlmProviderOptions options) => string.IsNullOrWhiteSpace(options.ApiKey);

    private AzureOpenAIClient AzureOpenAIClient(LlmProviderOptions options)
        => UsesEntraId(options)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), entraCredential.Value, clientOptions())
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey), clientOptions());

    private IChatClient CreateAzureOpenAIClient(LlmProviderOptions options, string modelName)
    {
        var client = AzureOpenAIClient(options);

        // A span and duration/token metrics per call (collected only when telemetry is on, see TelemetryExtensions).
        // Prompts and responses stay off the spans: that's the default, set explicitly so an OTEL_* setting can't change it.
        return client.GetChatClient(modelName).AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry(configure: telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }

    /// <summary>A shared embedding generator handed to a caller: disposing it leaves the shared one alive.</summary>
    private sealed class SharedEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> innerGenerator)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(innerGenerator)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>A shared client handed to a caller: disposing it leaves the shared client alive.</summary>
    private sealed class SharedChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
