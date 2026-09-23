using Microsoft.Extensions.AI;

namespace LazyDad.Api.Services;

public interface ILlmClientFactory
{
    IChatClient CreateClient(string providerName, string modelName);
}
