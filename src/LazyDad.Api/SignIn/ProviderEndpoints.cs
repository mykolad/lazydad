using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Facebook;

namespace LazyDad.Api.SignIn;

/// <summary>An OAuth provider's addresses: where readers sign in, the token exchange, and the user endpoint.</summary>
public sealed record OAuthEndpoints(string Authorize, string Token, string UserInfo);

/// <summary>
/// Where each provider is: its own addresses, or, in a load test (<c>SignIn:LoadTest:Authority</c>), the fake identity
/// provider's (<c>tests/load/FakeIdentityProvider.cs</c>), which plays all five under <c>&lt;authority&gt;/&lt;provider&gt;</c>.
/// Everything else about a provider (scopes, claims, checks) stays as in production, so the load test runs the real path.
/// </summary>
public sealed class ProviderEndpoints
{
    private readonly string? fake;

    private ProviderEndpoints(string? fake)
    {
        this.fake = fake?.TrimEnd('/');
        GitHub = this.fake is null
            ? new(GitHubAuthenticationDefaults.AuthorizationEndpoint, GitHubAuthenticationDefaults.TokenEndpoint, GitHubAuthenticationDefaults.UserInformationEndpoint)
            : Fake(SignInProviders.GitHub);
        Facebook = this.fake is null
            ? new(FacebookDefaults.AuthorizationEndpoint, FacebookDefaults.TokenEndpoint, FacebookDefaults.UserInformationEndpoint)
            : Fake(SignInProviders.Facebook);
        Google = FakeOr(SignInProviders.Google, OpenIdProviders.Google);
        Microsoft = FakeOr(SignInProviders.Microsoft, OpenIdProviders.Microsoft);
        Telegram = FakeOr(SignInProviders.Telegram, OpenIdProviders.Telegram);
    }

    /// <param name="loadTestAuthority">The fake identity provider's address, or empty for the real providers.</param>
    public static ProviderEndpoints For(string loadTestAuthority)
        => new(loadTestAuthority.Length > 0 ? loadTestAuthority : null);

    public OAuthEndpoints GitHub { get; }
    public OAuthEndpoints Facebook { get; }
    public OpenIdProvider Google { get; }
    public OpenIdProvider Microsoft { get; }
    public OpenIdProvider Telegram { get; }

    /// <summary>Where the Microsoft probe asks for a token as the app: the registration's own tenant.</summary>
    public string MicrosoftTenantToken(string tenantId)
        => fake is null ? OpenIdProviders.MicrosoftTenantTokenEndpoint(tenantId) : $"{fake}/{SignInProviders.Microsoft}/tenant-token";

    private OAuthEndpoints Fake(string provider) => new($"{fake}/{provider}/authorize", $"{fake}/{provider}/token", $"{fake}/{provider}/user");

    private OpenIdProvider FakeOr(string provider, OpenIdProvider real)
        => fake is null ? real : new($"{fake}/{provider}", $"{fake}/{provider}/token", $"{fake}/{provider}/authorize");
}
