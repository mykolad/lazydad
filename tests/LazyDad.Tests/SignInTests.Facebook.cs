using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LazyDad.Api.SignIn;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LazyDad.Tests;

/// <summary>Facebook, against a fake of its Graph API (<see cref="FakeFacebook"/>), and Meta's data-deletion callback.</summary>
public sealed partial class SignInTests
{
    private async Task<(HttpClient Client, WebApplication App, FakeFacebook Facebook)> StartWithFacebookAsync(string appSecret)
    {
        var facebook = new FakeFacebook();
        var settings = Settings(Pepper, "");
        settings["SignIn:Facebook:ClientId"] = FakeFacebook.AppId;
        settings["SignIn:Facebook:ClientSecret"] = appSecret;
        var app = await StartAsync(Environments.Development, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(SignInProviders.Facebook)).ConfigurePrimaryHttpMessageHandler(() => facebook));
        return (Client(app), app, facebook);
    }

    [Fact]
    public async Task SignIn_WithFacebook_AsksForThePublicProfileOnly_AndKeepsOnlyTheVoterKey()
    {
        var (client, app, facebook) = await StartWithFacebookAsync(FakeFacebook.AppSecret);

        var (authorize, cookies) = await StartRemoteSignInAsync(client, SignInProviders.Facebook, "/j/5");
        var query = QueryHelpers.ParseQuery(authorize.Query);
        using var response = await ProviderCallbackAsync(client, SignInProviders.Facebook, cookies,
            $"code={FakeFacebook.Code}&state={Uri.EscapeDataString(query["state"].ToString())}");

        Assert.Equal(FacebookDefaults.AuthorizationEndpoint, authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(FakeFacebook.AppId, query["client_id"].ToString());
        Assert.Equal(new Uri(client.BaseAddress!, "signin-facebook").AbsoluteUri, query["redirect_uri"].ToString());
        Assert.Equal("public_profile", query["scope"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        // Only the id is read, never the name or email.
        Assert.Equal("id", facebook.Fields);
        Assert.Equal("/j/5", response.Headers.Location!.OriginalString);
        var claims = Tickets(app).Unprotect(CookieValue(response))!.Principal.Claims.Select(c => (c.Type, c.Value)).Order().ToList();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Facebook, FakeFacebook.UserId);
        Assert.Equal([(SignInPrincipal.ProviderClaim, "facebook"), (SignInPrincipal.VoterClaim, Convert.ToBase64String(voterKey))], claims);
    }

    [Theory]
    [InlineData(FakeFacebook.AppSecret, ProviderState.Valid)]
    [InlineData("an-old-secret", ProviderState.Invalid)]
    public async Task Facebook_IsProbed_WithATokenAsTheApp(string appSecret, ProviderState expected)
    {
        var (_, app, _) = await StartWithFacebookAsync(appSecret);

        Assert.Equal(expected, await ProbedAsync(app, SignInProviders.Facebook));
    }

    // As Meta signs it: HMAC-SHA256 of the base64url payload, keyed with the app secret.
    private static string SignedRequest(string appSecret, string userId)
    {
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            algorithm = "HMAC-SHA256",
            expires = 0,
            issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            user_id = userId,
        }));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.ASCII.GetBytes(payload));
        return $"{WebEncoders.Base64UrlEncode(signature)}.{payload}";
    }

    private static async Task<HttpResponseMessage> FacebookDeletionAsync(HttpClient client, string signedRequest)
        => await client.PostAsync("auth/facebook/deletion", new FormUrlEncodedContent([new("signed_request", signedRequest)]));

    // One joke, voted up by the Facebook reader and by a Development reader. Returns its id.
    private async Task<int> SeedFacebookVotesAsync(WebApplication app)
    {
        var keys = app.Services.GetRequiredService<VoterKeys>();
        int jokeId;
        await using (var context = database.CreateContext())
        {
            var joke = new Data.Entities.Joke { Language = "Ukrainian", Model = "m", Text = "joke", GeneratedAt = DateTime.UtcNow };
            context.Jokes.Add(joke);
            await context.SaveChangesAsync();
            jokeId = joke.Id;
        }
        foreach (var key in new[] { keys.For(SignInProviders.Facebook, FakeFacebook.UserId), keys.For(SignInProviders.Development, "alice") })
        {
            await using var context = database.CreateContext();
            await new Data.Repositories.VoteRepository(context).SetAsync(jokeId, key, 1, DateTime.UtcNow, CancellationToken.None);
        }
        return jokeId;
    }

    [Fact]
    public async Task FacebookDeletion_WithMetasSignature_DeletesThatReadersVotes()
    {
        var (client, app, _) = await StartWithFacebookAsync(FakeFacebook.AppSecret);
        var jokeId = await SeedFacebookVotesAsync(app);

        using var response = await FacebookDeletionAsync(client, SignedRequest(FakeFacebook.AppSecret, FakeFacebook.UserId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new Uri(client.BaseAddress!, "privacy#facebook-deletion").AbsoluteUri, answer.RootElement.GetProperty("url").GetString());
        Assert.Matches("^[0-9a-f]{16}$", answer.RootElement.GetProperty("confirmation_code").GetString());
        await using var context = database.CreateContext();
        var alice = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "alice");
        Assert.Equal([alice], (await context.Votes.ToListAsync()).Select(v => v.VoterKey));
        Assert.Equal(1, (await context.Jokes.SingleAsync(j => j.Id == jokeId)).Up);
    }

    [Theory]
    [InlineData("another-secret")]
    [InlineData("")]
    public async Task FacebookDeletion_WithoutMetasSignature_IsRefused_AndDeletesNothing(string signingSecret)
    {
        var (client, app, _) = await StartWithFacebookAsync(FakeFacebook.AppSecret);
        await SeedFacebookVotesAsync(app);

        using var forged = await FacebookDeletionAsync(client, signingSecret.Length > 0 ? SignedRequest(signingSecret, FakeFacebook.UserId) : "not.signed");
        using var garbage = await FacebookDeletionAsync(client, "x");

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        await using var context = database.CreateContext();
        Assert.Equal(2, await context.Votes.CountAsync());
    }

    [Fact]
    public async Task FacebookDeletion_WithFacebookOff_IsNotFound()
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await FacebookDeletionAsync(client, SignedRequest(FakeFacebook.AppSecret, FakeFacebook.UserId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
