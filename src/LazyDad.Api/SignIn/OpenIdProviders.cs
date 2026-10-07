using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LazyDad.Api.SignIn;

/// <param name="Authority">Where the discovery document is (<c>/.well-known/openid-configuration</c>), and the issuer.</param>
/// <param name="TokenEndpoint">As the discovery document names it: the probe calls it directly, and the timing names it.</param>
/// <param name="AuthorizeEndpoint">Where readers are sent to sign in (the smoke tests check the redirect goes there).</param>
public sealed record OpenIdProvider(string Authority, string TokenEndpoint, string AuthorizeEndpoint);

/// <summary>The OpenID Connect providers' published endpoints (each one's discovery document says the same).</summary>
public static class OpenIdProviders
{
    public static readonly OpenIdProvider Google = new("https://accounts.google.com",
        "https://oauth2.googleapis.com/token", "https://accounts.google.com/o/oauth2/v2/auth");

    /// <summary>
    /// "common": work or school accounts from any tenant, and personal Microsoft accounts. Its discovery document names
    /// the issuer as a template (<c>…/{tenantid}/v2.0</c>), so tokens are checked by <see cref="MicrosoftIssuer"/>.
    /// </summary>
    public static readonly OpenIdProvider Microsoft = new("https://login.microsoftonline.com/common/v2.0",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize");

    /// <summary>Where the Microsoft probe asks for a token as the app: the registration's own tenant.</summary>
    public static string MicrosoftTenantTokenEndpoint(string tenantId) => $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

    /// <summary>
    /// With "common", a token's issuer must be its own tenant's (<c>https://login.microsoftonline.com/{tid}/v2.0</c>,
    /// with the token's <c>tid</c>), as Microsoft's guidance for multi-tenant apps says; any other issuer is refused.
    /// </summary>
    public static string MicrosoftIssuer(string issuer, SecurityToken token,
        TokenValidationParameters parameters)
    {
        var tenant = token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid) ? tid : null;
        if (Guid.TryParse(tenant, out _) && issuer == $"https://login.microsoftonline.com/{tenant}/v2.0")
            return issuer;
        throw new SecurityTokenInvalidIssuerException("The id token's issuer isn't its own tenant's.");
    }
}
