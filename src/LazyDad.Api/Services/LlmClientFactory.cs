using Azure;
using Azure.AI.OpenAI;
using LazyDad.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class LlmClientFactory : ILlmClientFactory
{
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

    private static IChatClient CreateAzureOpenAIClient(LlmProviderOptions options, string modelName)
    {
        var client = new AzureOpenAIClient(
            new Uri(options.Endpoint),
            new AzureKeyCredential(options.ApiKey));

        return client.GetChatClient(modelName).AsIChatClient();
    }
}
