#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.IdentityModel.JsonWebTokens@8.19.2
#:property PublishAot=false

// The load test's identity provider: plays all five sign-in providers so the load-test app's whole sign-in path (the
// redirect, the callback, the token exchange, the id token's checks, the cookie) runs under load without touching the
// real ones, which would block scripted logins. The Load Test Environment workflow runs it as a second Container App and
// points the load-test app at it (SignIn:LoadTest:Authority; the app refuses that setting anywhere else).
//
//   dotnet run tests/load/FakeIdentityProvider.cs                     (http://localhost:5000, or ASPNETCORE_URLS)
//
// Each provider lives under /<provider>: Google, Microsoft and Telegram as OpenID Connect (discovery, signing keys, a
// signed id token), GitHub and Facebook as their OAuth endpoints and user endpoint. /authorize approves at once, with no
// login page, as a new random account. It keeps no state: the account and the nonce travel inside the code it hands out,
// so any number of replicas, and restarts between a redirect and its callback, are fine. It checks no client id, secret
// or code verifier: the load test measures the app, not the provider. The real providers' speed comes from production's
// metrics (lazydad_signin_provider_duration_seconds).

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
// Behind Container Apps' ingress, the discovery document must name the https address readers reach.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
var app = builder.Build();
app.UseForwardedHeaders();

var signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) };
string[] openIdProviders = ["google", "microsoft", "telegram"];

string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}";

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

// Readers go back only to the load-test app's sign-in callbacks: it's open to everyone, and would be an open redirect
// otherwise. ALLOWED_REDIRECT_HOST is the app's host (the workflow sets it); without it, only this machine's.
var allowedRedirectHost = app.Configuration["ALLOWED_REDIRECT_HOST"] ?? "localhost";
string[] providers = ["github", "google", "microsoft", "telegram", "facebook"];

// — authorize: every provider approves at once, as a new account —
app.MapGet("/{provider}/authorize", (string provider, HttpRequest request) =>
{
    var query = request.Query;
    var redirect = query["redirect_uri"].ToString();
    if (!providers.Contains(provider))
        return Results.NotFound();
    if (!Uri.TryCreate(redirect, UriKind.Absolute, out var callback) || callback.Scheme is not ("https" or "http")
        || !string.Equals(callback.Host, allowedRedirectHost, StringComparison.OrdinalIgnoreCase)
        || callback.AbsolutePath != $"/signin-{provider}")
        return Results.BadRequest($"redirect_uri must be http(s)://{allowedRedirectHost}[:port]/signin-{provider}.");
    var code = Code.Write(new Grant(provider, RandomAccount(), query["nonce"].ToString(), query["client_id"].ToString()));
    return Results.Redirect(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
});

// — OpenID Connect: Google, Microsoft, Telegram —
foreach (var provider in openIdProviders)
{
    app.MapGet($"/{provider}/.well-known/openid-configuration", (HttpRequest request) =>
    {
        var authority = $"{Origin(request)}/{provider}";
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer(provider, authority),
            ["authorization_endpoint"] = $"{authority}/authorize",
            ["token_endpoint"] = $"{authority}/token",
            ["jwks_uri"] = $"{authority}/keys",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        });
    });
    app.MapGet($"/{provider}/keys", () =>
    {
        var publicKey = new RsaSecurityKey(signingKey.Rsa.ExportParameters(false)) { KeyId = signingKey.KeyId };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
        return Results.Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
    });
}

app.MapPost("/{provider}/token", async (string provider, HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    if (form["grant_type"] == "client_credentials")
        return Results.Json(new { access_token = "app-token", token_type = "Bearer", expires_in = 3600 });
    if (!Code.TryRead(form["code"].ToString(), out var grant) || grant.Provider != provider)
        return provider switch
        {
            // As the real ones answer the probes' made-up code: GitHub and Telegram with 200 and the error in the body.
            "github" => Results.Json(new { error = "bad_verification_code" }),
            "telegram" => Results.Json(new { error = "invalid_grant" }),
            "facebook" => Results.Json(new { error = new { message = "Invalid verification code format.", type = "OAuthException", code = 100 } }, statusCode: 400),
            _ => Results.Json(new { error = "invalid_grant" }, statusCode: 400),
        };

    var accessToken = $"{provider}-{grant.Account}";
    if (!openIdProviders.Contains(provider))
        return Results.Json(new { access_token = accessToken, token_type = "bearer", expires_in = 3600 });
    var claims = new Dictionary<string, object> { ["sub"] = grant.Account };
    if (grant.Nonce.Length > 0)
        claims["nonce"] = grant.Nonce;
    if (provider == "microsoft")
        claims["tid"] = MicrosoftTenantId.Value;
    var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer(provider, $"{Origin(request)}/{provider}"),
        Audience = grant.ClientId,
        IssuedAt = DateTime.UtcNow,
        NotBefore = DateTime.UtcNow.AddMinutes(-1),
        Expires = DateTime.UtcNow.AddMinutes(10),
        Claims = claims,
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
    });
    return Results.Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 3600, id_token = idToken });
});

// Microsoft's probe asks the registration's own tenant for a token as the app.
app.MapPost("/microsoft/tenant-token", () => Results.Json(new { access_token = "app-token", token_type = "Bearer", expires_in = 3600 }));

// — the user endpoints: GitHub's (the token in the Authorization header) and Facebook's (in the query) —
app.MapGet("/github/user", (HttpRequest request) =>
{
    var token = request.Headers.Authorization.ToString().Split(' ').LastOrDefault() ?? "";
    return token.StartsWith("github-", StringComparison.Ordinal)
        ? Results.Json(new { id = long.Parse(token["github-".Length..]), login = $"loadtest{token["github-".Length..]}" })
        : Results.Json(new { message = "Bad credentials" }, statusCode: 401);
});
app.MapGet("/facebook/user", (HttpRequest request) =>
{
    var token = request.Query["access_token"].ToString();
    return token.StartsWith("facebook-", StringComparison.Ordinal)
        ? Results.Json(new { id = token["facebook-".Length..] })
        : Results.Json(new { error = new { message = "Invalid OAuth access token.", type = "OAuthException", code = 190 } }, statusCode: 400);
});

app.Run();

static string Issuer(string provider, string authority)
    => provider == "microsoft" ? $"https://login.microsoftonline.com/{MicrosoftTenantId.Value}/v2.0" : authority;

// A positive number, like GitHub's and Facebook's ids; plenty of accounts for any load test.
static string RandomAccount() => (RandomNumberGenerator.GetInt32(1, int.MaxValue) * 1000L + RandomNumberGenerator.GetInt32(1000)).ToString();

/// <summary>What a code stands for: the sign-in it answers.</summary>
record Grant(string Provider, string Account, string Nonce, string ClientId);

/// <summary>The code is the grant itself, base64url JSON: nothing to remember between /authorize and /token.</summary>
static class Code
{
    public static string Write(Grant grant) => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(grant));

    public static bool TryRead(string code, out Grant grant)
    {
        grant = new Grant("", "", "", "");
        try
        {
            if (JsonSerializer.Deserialize<Grant>(WebEncoders.Base64UrlDecode(code)) is not { Account.Length: > 0 } read)
                return false;
            grant = read;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>Microsoft's id tokens name their own tenant as the issuer, which the app checks; any tenant will do.</summary>
static class MicrosoftTenantId
{
    public const string Value = "4a3f1c2b-0000-4000-8000-00000000f4c3";
}
