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
    // Entra ID when no API key is configured: the app's managed identity in Azure, the developer's
    // `az login` locally. One instance, so its token cache is shared by every client.
    private static readonly Lazy<TokenCredential> EntraCredential = new(() => new DefaultAzureCredential());

    private readonly IOptions<Dictionary<string, LlmProviderOptions>> providers;

    public LlmClientFactory(IOptions<Dictionary<string, LlmProviderOptions>> providers)
    {
        this.providers = providers;
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

    private static IChatClient CreateAzureOpenAIClient(LlmProviderOptions options, string modelName)
    {
        var client = UsesEntraId(options)
            ? new AzureOpenAIClient(new Uri(options.Endpoint), EntraCredential.Value)
            : new AzureOpenAIClient(new Uri(options.Endpoint), new AzureKeyCredential(options.ApiKey));

        return client.GetChatClient(modelName).AsIChatClient();
    }
}
