using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyDad.Api.SignIn;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LazyDad.Tests;

/// <summary>
/// An OpenID Connect provider's back channel, as the handler and the probe call it: the discovery document, the signing
/// keys, and the token endpoint, which answers a code with a signed id token. Plugged in as the primary handler of the
/// provider's back channel client (SignInBackchannel), so a sign-in completes without leaving the test.
/// </summary>
public sealed class FakeOpenIdProvider : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string ClientSecret = "test-client-secret";
    public const string Code = "the-code";
    public const string KeyId = "test-key";
    /// <summary>The managed identity's token it accepts instead of the secret (a federated credential).</summary>
    public const string Assertion = "test-managed-identity-token";

    private readonly OpenIdProvider endpoints;
    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = KeyId };

    public FakeOpenIdProvider(OpenIdProvider endpoints)
    {
        this.endpoints = endpoints;
        Issuer = endpoints.Authority;
    }

    public string JwksUri => $"{endpoints.Authority}/test-keys";

    /// <summary>The account id the id token carries.</summary>
    public string? Subject { get; set; } = "110169484474386276334";

    /// <summary>The nonce to put in the id token: what the handler sent the reader to the provider with.</summary>
    public string? Nonce { get; set; }

    public string Issuer { get; set; }
    public string Audience { get; set; } = ClientId;

    /// <summary>Signs with a key the published set doesn't hold, like a forged token.</summary>
    public bool SignWithAnotherKey { get; set; }

    /// <summary>More claims in the id token, as a provider adds them (Microsoft's tid, say).</summary>
    public Dictionary<string, object> ExtraClaims { get; } = [];

    /// <summary>The last successful token exchange's form, as sent.</summary>
    public Dictionary<string, string> TokenRequest { get; private set; } = [];

    /// <summary>Answers errors with 200 and the error in the body, as Telegram (and GitHub) do.</summary>
    public bool ErrorsWith200 { get; set; }

    /// <summary>Where it gives the app a token for itself (the client credentials grant), if anywhere: Microsoft's probe.</summary>
    public string? ClientCredentialsEndpoint { get; set; }

    // The client proves itself with its secret, or with the managed identity's token.
    private static bool Authenticated(Dictionary<string, string> form)
        => form.GetValueOrDefault("client_id") == ClientId
            && (form.GetValueOrDefault("client_secret") == ClientSecret
                || (form.GetValueOrDefault("client_assertion_type") == IClientAssertion.Type && form.GetValueOrDefault("client_assertion") == Assertion));

    private static async Task<Dictionary<string, string>> FormAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
            .Select(pair => pair.Split('='))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.AbsoluteUri;
        if (uri == $"{endpoints.Authority}/.well-known/openid-configuration")
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["issuer"] = endpoints.Authority,
                ["authorization_endpoint"] = endpoints.AuthorizeEndpoint,
                ["token_endpoint"] = endpoints.TokenEndpoint,
                ["jwks_uri"] = JwksUri,
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            }));
        if (uri == JwksUri)
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(PublicKey()) } },
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        if (request.Method == HttpMethod.Post && uri == ClientCredentialsEndpoint)
        {
            var form = await FormAsync(request, cancellationToken);
            return Authenticated(form) && form.GetValueOrDefault("grant_type") == "client_credentials"
                ? Json(HttpStatusCode.OK, """{"access_token": "test-app-token", "token_type": "Bearer", "expires_in": 3600}""")
                : Json(HttpStatusCode.Unauthorized, """{"error": "invalid_client"}""");
        }
        if (request.Method == HttpMethod.Post && uri == endpoints.TokenEndpoint)
        {
            var form = await FormAsync(request, cancellationToken);
            if (!Authenticated(form))
                return Json(ErrorsWith200 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, """{"error": "invalid_client"}""");
            if (form.GetValueOrDefault("code") != Code)
                return Json(ErrorsWith200 ? HttpStatusCode.OK : HttpStatusCode.BadRequest, """{"error": "invalid_grant"}""");
            TokenRequest = form;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                access_token = "test-access-token",
                token_type = "Bearer",
                expires_in = 3600,
                id_token = IdToken(),
            }));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private RsaSecurityKey PublicKey() => new(key.Rsa.ExportParameters(false)) { KeyId = KeyId };

    private string IdToken()
    {
        var claims = new Dictionary<string, object>(ExtraClaims)
        {
            // What a provider would add with more scopes; none of it may reach the cookie.
            ["name"] = "Alice Example",
            ["email"] = "alice@example.com",
        };
        if (Subject is not null)
            claims["sub"] = Subject;
        if (Nonce is not null)
            claims["nonce"] = Nonce;
        var signingKey = SignWithAnotherKey ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = KeyId } : key;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
