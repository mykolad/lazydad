using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LazyDad.Api.Controllers;
using LazyDad.Api.SignIn;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LazyDad.Tests;

/// <summary>
/// The smoke tests' sign-in (SmokeSignIn): an Entra token of the deploy identity, accepted only on <c>/me/votes</c>, and
/// <c>GET /me/votes</c> itself. Tokens are signed with a test key the app is given as Entra's.
/// </summary>
public sealed partial class SignInTests
{
    private const string SmokeTenant = "3f1a6c2e-5b7d-4e8f-9a0b-1c2d3e4f5a6b";
    private const string SmokeAudience = "api://lazydad-smoke";
    private const string DeployIdentity = "0b7c9a52-3d4e-4f60-8a71-92b3c4d5e6f7";

    private static readonly RsaSecurityKey EntraKey = new(RSA.Create(2048)) { KeyId = "entra-test-key" };

    private async Task<(HttpClient Client, WebApplication App)> StartWithSmokeSignInAsync(bool on)
    {
        var settings = Settings(Pepper, "");
        if (on)
        {
            settings["SignIn:Smoke:TenantId"] = SmokeTenant;
            settings["SignIn:Smoke:Audience"] = SmokeAudience;
            settings["SignIn:Smoke:AllowedObjectIds:0"] = DeployIdentity;
        }
        var app = await StartAsync(Environments.Development, settings, services =>
            // Entra's signing keys, without fetching its metadata.
            services.Configure<JwtBearerOptions>(SignInProviders.Smoke, jwt =>
            {
                jwt.Configuration = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{SmokeTenant}/v2.0" };
                jwt.Configuration.SigningKeys.Add(EntraKey);
            }));
        return (Client(app), app);
    }

    private static string EntraToken(string audience, string objectId, string tenant, string issuer, DateTime expires, SecurityKey key)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expires.AddHours(-1),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            Claims = new Dictionary<string, object> { ["oid"] = objectId, ["tid"] = tenant, ["appid"] = "deploy-identity-client-id" },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });

    // What `az account get-access-token --resource api://lazydad-smoke` gives the deploy identity (a v1 token).
    private static string DeployToken()
        => EntraToken(SmokeAudience, DeployIdentity, SmokeTenant, $"https://sts.windows.net/{SmokeTenant}/", DateTime.UtcNow.AddHours(1), EntraKey);

    private static async Task<HttpResponseMessage> MyVotesAsync(HttpClient client, string query, string? bearer, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"me/votes{query}");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{SignInSetup.CookieName}={cookie}");
        return await client.SendAsync(request);
    }

    // A joke with the smoke voter's vote on it. Returns its id.
    private async Task<int> SeedSmokeVoteAsync(WebApplication app, int value)
    {
        int jokeId;
        await using (var context = database.CreateContext())
        {
            var joke = new Data.Entities.Joke { Language = "Ukrainian", Model = "m", Text = "joke", GeneratedAt = DateTime.UtcNow };
            context.Jokes.Add(joke);
            await context.SaveChangesAsync();
            jokeId = joke.Id;
        }
        await using (var context = database.CreateContext())
            await new Data.Repositories.VoteRepository(context).SetAsync(jokeId,
                app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Smoke, SmokeSignIn.AccountId), value, DateTime.UtcNow, CancellationToken.None);
        return jokeId;
    }

    [Fact]
    public async Task MyVotes_WithTheDeployIdentitysToken_AreTheSmokeVotersVotes()
    {
        var (client, app) = await StartWithSmokeSignInAsync(on: true);
        var jokeId = await SeedSmokeVoteAsync(app, -1);

        using var response = await MyVotesAsync(client, $"?ids={jokeId},{jokeId + 1000}", DeployToken(), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal($$"""{"{{jokeId}}":-1}""", await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<string> RefusedTokens() => ["audience", "identity", "tenant", "issuer", "expired", "signature"];

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task MyVotes_WithAnyOtherToken_IsUnauthorized(string difference)
    {
        var (client, _) = await StartWithSmokeSignInAsync(on: true);
        var issuer = $"https://sts.windows.net/{SmokeTenant}/";
        var token = difference switch
        {
            "audience" => EntraToken("api://another-app", DeployIdentity, SmokeTenant, issuer, DateTime.UtcNow.AddHours(1), EntraKey),
            // Anyone in the tenant can get a token for the audience: a developer, another app's identity.
            "identity" => EntraToken(SmokeAudience, Guid.NewGuid().ToString(), SmokeTenant, issuer, DateTime.UtcNow.AddHours(1), EntraKey),
            "tenant" => EntraToken(SmokeAudience, DeployIdentity, Guid.NewGuid().ToString(), issuer, DateTime.UtcNow.AddHours(1), EntraKey),
            "issuer" => EntraToken(SmokeAudience, DeployIdentity, SmokeTenant, $"https://sts.windows.net/{Guid.NewGuid()}/", DateTime.UtcNow.AddHours(1), EntraKey),
            "expired" => EntraToken(SmokeAudience, DeployIdentity, SmokeTenant, issuer, DateTime.UtcNow.AddHours(-1), EntraKey),
            _ => EntraToken(SmokeAudience, DeployIdentity, SmokeTenant, issuer, DateTime.UtcNow.AddHours(1), new RsaSecurityKey(RSA.Create(2048)) { KeyId = EntraKey.KeyId }),
        };

        using var response = await MyVotesAsync(client, "?ids=1", token, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheDeployIdentitysToken_IsNotASignInAnywhereElse()
    {
        var (client, _) = await StartWithSmokeSignInAsync(on: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeployToken());

        using var response = await client.SendAsync(request);

        Assert.False(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task WithTheSmokeSignInOff_EvenTheDeployIdentitysToken_IsUnauthorized()
    {
        var (client, _) = await StartWithSmokeSignInAsync(on: false);

        using var response = await MyVotesAsync(client, "?ids=1", DeployToken(), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MyVotes_ForASignedInReader_AreTheirOwn_AndSignedOutIsUnauthorized()
    {
        var app = await StartDevelopmentAsync();
        var (first, second) = await SeedVotesAsync(app);
        var client = Client(app);
        var cookie = await SignInAsync(client, "alice");

        using var mine = await MyVotesAsync(client, $"?ids={first},{second}", null, cookie);
        using var signedOut = await MyVotesAsync(client, $"?ids={first}", null, null);

        var votes = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, votes.GetProperty(first.ToString()).GetInt32());
        Assert.Equal(-1, votes.GetProperty(second.ToString()).GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Theory]
    [InlineData("?ids=1,x")]
    [InlineData("?ids=-1")]
    [InlineData("?ids=1.5")]
    public async Task MyVotes_WithIdsThatAreNotJokeIds_IsABadRequest(string query)
    {
        var app = await StartDevelopmentAsync();
        var client = Client(app);

        using var response = await MyVotesAsync(client, query, null, await SignInAsync(client, "alice"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MyVotes_ForMoreThanAPage_IsABadRequest()
    {
        var app = await StartDevelopmentAsync();
        var client = Client(app);
        var ids = string.Join(',', Enumerable.Range(1, SignInController.MaxVoteIds + 1));

        using var tooMany = await MyVotesAsync(client, $"?ids={ids}", null, await SignInAsync(client, "alice"));
        using var none = await MyVotesAsync(client, "", null, await SignInAsync(client, "alice"));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("{}", await none.Content.ReadAsStringAsync());
    }
}
