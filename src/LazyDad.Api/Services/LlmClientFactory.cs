using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using LazyDad.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class LlmClientFactory : ILlmClientFactory
{
    private readonly IOptions<Dictionary<string, LlmProviderOptions>> providers;
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

        return providerName switch
        {
            "AzureOpenAI" => CreateAzureOpenAIClient(options, modelName),
            _ => throw new NotSupportedException($"LLM provider '{providerName}' is not supported.")
        };
    }

    /// <summary>
    /// The API key while one is configured, else Entra ID. Removing the key setting switches an
    /// environment over (issue #10) without a code change.
    /// </summary>
    internal static bool UsesEntraId(LlmProviderOptions options) => string.IsNullOrWhiteSpace(options.ApiKey);

    private IChatClient CreateAzureOpenAIClient(LlmProviderOptions options, string modelName)
    {
        var client = UsesEntraId(options)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), entraCredential.Value, clientOptions())
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey), clientOptions());

        return client.GetChatClient(modelName).AsIChatClient();
    }
}
