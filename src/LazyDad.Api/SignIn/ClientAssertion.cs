using Azure.Core;
using Azure.Identity;

namespace LazyDad.Api.SignIn;

/// <summary>
/// What an app sends a provider instead of a client secret: a token that proves who it is (RFC 7523's
/// <c>client_assertion</c>). Microsoft's only, today.
/// </summary>
public interface IClientAssertion
{
    public const string Type = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    Task<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A token of the app's user-assigned managed identity for <c>api://AzureADTokenExchange</c>, which the Entra app
/// registration trusts as a federated credential: no secret to store, expire or rotate. Azure.Identity caches it.
/// </summary>
public sealed class ManagedIdentityAssertion(string managedIdentityClientId) : IClientAssertion
{
    private static readonly TokenRequestContext Request = new(["api://AzureADTokenExchange/.default"]);

    private readonly ManagedIdentityCredential credential = new(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));

    public async Task<string> GetAsync(CancellationToken cancellationToken)
        => (await credential.GetTokenAsync(Request, cancellationToken)).Token;
}
