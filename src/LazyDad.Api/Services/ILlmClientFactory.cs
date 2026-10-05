using Microsoft.Extensions.AI;

namespace LazyDad.Api.Services;

public interface ILlmClientFactory
{
    IChatClient CreateClient(string providerName, string modelName);

    /// <summary>An embedding model's client (the similar jokes' fallback), shared like the chat clients.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string providerName, string deploymentName);
}
