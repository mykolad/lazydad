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
}
