using System.Net;
using System.Text.Json;
using LazyDad.Api.Controllers;
using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using LazyDad.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

/// <summary>
/// The sign-in endpoints and cookie in a real app (Kestrel on a free port), wired as Program.cs wires them, with the
/// key ring in a test database. Cookies are handled by hand: they're Secure, and the test talks plain HTTP.
/// </summary>
public sealed class SignInTests : IAsyncDisposable
{
    private const string KeyId = "https://lazydad-kv.vault.azure.net/keys/DataProtection";
    private static readonly string Pepper = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private readonly TestDatabase database = new();
    private readonly List<WebApplication> apps = [];
    private readonly List<HttpClient> clients = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients)
            client.Dispose();
        foreach (var app in apps)
            await app.DisposeAsync();
        database.Dispose();
    }

    private Task<WebApplication> StartAsync(string environment, string pepper, string keyId)
        => StartAsync(environment, Settings(pepper, keyId), _ => { });

    private static Dictionary<string, string?> Settings(string pepper, string keyId) => new()
    {
        ["SignIn:VoterKeyPepper"] = pepper,
        ["DataProtection:KeyVaultKeyId"] = keyId,
    };

    private async Task<WebApplication> StartAsync(string environment, Dictionary<string, string?> settings, Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDbContext<LazyDadDbContext>(database.Configure);
        builder.Services.AddScoped<LazyDad.Data.Repositories.IVoteRepository, LazyDad.Data.Repositories.VoteRepository>();
        builder.Services.AddControllers().AddApplicationPart(typeof(SignInController).Assembly);
        builder.AddSignIn();
        configure(builder.Services);
        var app = builder.Build();
        apps.Add(app);
        app.Services.GetRequiredService<SignInMetrics>();
        app.UseAuthentication();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private Task<WebApplication> StartDevelopmentAsync() => StartAsync(Environments.Development, Pepper, "");

    // As production keeps the key ring (a Key Vault key id configured), but with the keys left unencrypted, so the test
    // needs no Key Vault.
    private Task<WebApplication> StartWithTheKeyRingInTheDatabaseAsync()
        => StartAsync(Environments.Development, Settings(Pepper, KeyId),
            services => services.PostConfigure<KeyManagementOptions>(options => options.XmlEncryptor = null));

    private HttpClient Client(WebApplication app)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };
        clients.Add(client);
        return client;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{SignInSetup.CookieName}={cookie}");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> MeAsync(HttpClient client, string? cookie)
    {
        using var response = await GetAsync(client, "me", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static string SetCookie(HttpResponseMessage response)
        => Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{SignInSetup.CookieName}=", StringComparison.Ordinal));

    private static string CookieValue(HttpResponseMessage response)
        => SetCookie(response).Split(';')[0][(SignInSetup.CookieName.Length + 1)..];

    // Signs in with the Development provider and returns the cookie.
    private static async Task<string> SignInAsync(HttpClient client, string account)
    {
        using var response = await GetAsync(client, $"auth/signin/dev?account={account}&returnUrl=/", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return CookieValue(response);
    }

    private static TicketDataFormat Tickets(WebApplication app)
        => (TicketDataFormat)app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;

    [Fact]
    public async Task Me_WhenSignedOut_SaysSo_AndIsNeverCached()
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await GetAsync(client, "me", null);

        var me = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(me.GetProperty("signedIn").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("provider").ValueKind);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task SignIn_WithTheDevelopmentProvider_SetsASessionCookie_AndReturnsToThePage()
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await GetAsync(client, "auth/signin/dev?returnUrl=%2Fj%2F5", null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/j/5", response.Headers.Location!.OriginalString);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var cookie = SetCookie(response).ToLowerInvariant();
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("domain=", cookie);
        // A session cookie: gone when the browser closes, unless the reader ticks "Keep me signed in".
        Assert.DoesNotContain("expires=", cookie);

        var me = await MeAsync(client, CookieValue(response));
        Assert.True(me.GetProperty("signedIn").GetBoolean());
        Assert.Equal("dev", me.GetProperty("provider").GetString());
    }

    // When the cookie in this answer expires, or null for a session cookie.
    private static DateTimeOffset? Expires(HttpResponseMessage response)
    {
        var attribute = SetCookie(response).Split(';').Select(a => a.Trim())
            .FirstOrDefault(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return attribute is null ? null : DateTimeOffset.Parse(attribute["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task KeepMeSignedIn_MakesTheCookieLast90Days()
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await GetAsync(client, "auth/signin/dev?persist=true&returnUrl=/", null);

        var expires = Assert.NotNull(Expires(response));
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromDays(90) - TimeSpan.FromMinutes(5), TimeSpan.FromDays(90));
        Assert.True((await MeAsync(client, CookieValue(response))).GetProperty("signedIn").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASignInInUse_IsRenewedOnceHalfItsLifetimeHasPassed_AndKeepsItsKind(bool persistent)
    {
        var app = await StartDevelopmentAsync();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "alice");
        var issued = DateTimeOffset.UtcNow - TimeSpan.FromDays(50);
        var old = Tickets(app).Protect(new AuthenticationTicket(SignInPrincipal.Create(SignInProviders.Development, voterKey),
            new AuthenticationProperties { IsPersistent = persistent, IssuedUtc = issued, ExpiresUtc = issued + SignInSetup.Lifetime },
            CookieAuthenticationDefaults.AuthenticationScheme));

        using var response = await GetAsync(Client(app), "me", old);

        var renewed = Tickets(app).Unprotect(CookieValue(response))!;
        Assert.InRange(renewed.Properties.ExpiresUtc!.Value - DateTimeOffset.UtcNow, TimeSpan.FromDays(89), TimeSpan.FromDays(90));
        Assert.Equal(persistent, Expires(response) is not null);
    }

    [Fact]
    public async Task ASignInNotYetHalfwayThrough_IsNotRenewed()
    {
        var app = await StartDevelopmentAsync();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "alice");
        var issued = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        var recent = Tickets(app).Protect(new AuthenticationTicket(SignInPrincipal.Create(SignInProviders.Development, voterKey),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = issued, ExpiresUtc = issued + SignInSetup.Lifetime },
            CookieAuthenticationDefaults.AuthenticationScheme));

        using var response = await GetAsync(Client(app), "me", recent);

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.example%2F")]
    [InlineData("%2F%2Fevil.example")]
    [InlineData("%2F%5Cevil.example")]
    [InlineData("")]
    public async Task SignIn_ReturnsOnlyToThisSite(string returnUrl)
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await GetAsync(client, $"auth/signin/dev?returnUrl={returnUrl}", null);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("microsoft")]
    [InlineData("github")]
    public async Task SignIn_WithAProviderThatIsNotEnabled_IsNotFound(string provider)
    {
        var client = Client(await StartDevelopmentAsync());

        using var response = await GetAsync(client, $"auth/signin/{provider}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_ThereIsNoDevelopmentProvider()
    {
        var client = Client(await StartAsync(Environments.Production, Pepper, KeyId));

        using var response = await GetAsync(client, "auth/signin/dev", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await MeAsync(client, null)).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task WithSignInOff_NobodyCanSignIn()
    {
        var client = Client(await StartAsync(Environments.Development, "", ""));

        using var response = await GetAsync(client, "auth/signin/dev", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Startup_WithSignInOnButNoKeyVaultKey_FailsOutsideDevelopment()
        => await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(Environments.Production, Pepper, ""));

    [Fact]
    public async Task TheCookie_HoldsOnlyTheVoterKeyAndTheProvider()
    {
        var app = await StartDevelopmentAsync();

        var cookie = await SignInAsync(Client(app), "alice");

        var ticket = Tickets(app).Unprotect(cookie)!;
        var claims = ticket.Principal.Claims.Select(c => (c.Type, c.Value)).Order().ToList();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "alice");
        Assert.Equal([(SignInPrincipal.ProviderClaim, "dev"), (SignInPrincipal.VoterClaim, Convert.ToBase64String(voterKey))], claims);
        Assert.DoesNotContain(claims, c => c.Value.Contains("alice"));
    }

    [Fact]
    public async Task SignOut_RemovesTheCookie()
    {
        var client = Client(await StartDevelopmentAsync());
        var cookie = await SignInAsync(client, "alice");

        using var request = new HttpRequestMessage(HttpMethod.Post, "auth/signout");
        request.Headers.Add("Cookie", $"{SignInSetup.CookieName}={cookie}");
        request.Headers.Add(SignInController.RequestHeader, SignInController.RequestHeaderValue);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var removal = SetCookie(response).ToLowerInvariant();
        Assert.StartsWith($"{SignInSetup.CookieName.ToLowerInvariant()}=;", removal);
        Assert.Contains("expires=thu, 01 jan 1970", removal);
    }

    private static async Task<HttpResponseMessage> DeleteMyVotesAsync(HttpClient client, string? cookie, bool fromThePage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "me/votes");
        if (cookie is not null)
            request.Headers.Add("Cookie", $"{SignInSetup.CookieName}={cookie}");
        if (fromThePage)
            request.Headers.Add(SignInController.RequestHeader, SignInController.RequestHeaderValue);
        return await client.SendAsync(request);
    }

    // Two jokes; alice votes up on the first and down on the second, bob up on the first. Returns their ids.
    private async Task<(int First, int Second)> SeedVotesAsync(WebApplication app)
    {
        var keys = app.Services.GetRequiredService<VoterKeys>();
        int first, second;
        await using (var context = database.CreateContext())
        {
            var a = new Data.Entities.Joke { Language = "Ukrainian", Model = "m", Text = "first", GeneratedAt = DateTime.UtcNow };
            var b = new Data.Entities.Joke { Language = "Ukrainian", Model = "m", Text = "second", GeneratedAt = DateTime.UtcNow };
            context.Jokes.AddRange(a, b);
            await context.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
        }
        foreach (var (joke, account, value) in new[] { (first, "alice", 1), (second, "alice", -1), (first, "bob", 1) })
        {
            await using var context = database.CreateContext();
            await new Data.Repositories.VoteRepository(context)
                .SetAsync(joke, keys.For(SignInProviders.Development, account), value, DateTime.UtcNow, CancellationToken.None);
        }
        return (first, second);
    }

    [Fact]
    public async Task DeleteMyVotes_RemovesOnlyThisReadersVotes_AndTakesThemOffTheCounts()
    {
        var app = await StartDevelopmentAsync();
        var (first, second) = await SeedVotesAsync(app);
        var client = Client(app);
        var cookie = await SignInAsync(client, "alice");

        using var response = await DeleteMyVotesAsync(client, cookie, fromThePage: true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        await using var context = database.CreateContext();
        var bob = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "bob");
        Assert.Equal([bob], (await context.Votes.ToListAsync()).Select(v => v.VoterKey));
        var jokes = await context.Jokes.ToDictionaryAsync(j => j.Id);
        Assert.Equal((1, 0), (jokes[first].Up, jokes[first].Down));
        Assert.Equal((0, 0), (jokes[second].Up, jokes[second].Down));
        // Signed in still: deleting the votes isn't signing out.
        Assert.True((await MeAsync(client, cookie)).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task DeleteMyVotes_Twice_ChangesNothingTheSecondTime()
    {
        // A retry after a lost answer.
        var app = await StartDevelopmentAsync();
        var (first, _) = await SeedVotesAsync(app);
        var client = Client(app);
        var cookie = await SignInAsync(client, "alice");

        using var once = await DeleteMyVotesAsync(client, cookie, fromThePage: true);
        using var twice = await DeleteMyVotesAsync(client, cookie, fromThePage: true);

        Assert.Equal(HttpStatusCode.NoContent, twice.StatusCode);
        await using var context = database.CreateContext();
        Assert.Equal(1, (await context.Jokes.SingleAsync(j => j.Id == first)).Up);
    }

    [Fact]
    public async Task DeleteMyVotes_SignedOut_IsUnauthorized_AndWithoutThePagesHeader_IsRefused()
    {
        var app = await StartDevelopmentAsync();
        await SeedVotesAsync(app);
        var client = Client(app);
        var cookie = await SignInAsync(client, "alice");

        using var signedOut = await DeleteMyVotesAsync(client, null, fromThePage: true);
        using var fromAnotherSite = await DeleteMyVotesAsync(client, cookie, fromThePage: false);

        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fromAnotherSite.StatusCode);
        await using var context = database.CreateContext();
        Assert.Equal(3, await context.Votes.CountAsync());
    }

    [Fact]
    public async Task SignOut_WithoutThePagesHeader_IsRefused()
    {
        // What another site's form can send: the reader's cookie stays.
        var client = Client(await StartDevelopmentAsync());

        using var response = await client.PostAsync("auth/signout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ASignInOnOneApp_CountsOnAnotherAppWithTheSameDatabase()
    {
        // The two production apps: a sign-in that starts in one region can land in the other.
        var first = await StartWithTheKeyRingInTheDatabaseAsync();
        var second = await StartWithTheKeyRingInTheDatabaseAsync();

        var cookie = await SignInAsync(Client(first), "alice");

        Assert.True((await MeAsync(Client(second), cookie)).GetProperty("signedIn").GetBoolean());
        await using var context = database.CreateContext();
        Assert.NotEmpty(await context.DataProtectionKeys.ToListAsync());
    }

    [Fact]
    public async Task ALocalRunWithoutAKeyVaultKey_KeepsItsKeyRingOutOfTheDatabase()
    {
        // A local run often points at production's database: its unencrypted keys must never join production's ring.
        var cookie = await SignInAsync(Client(await StartDevelopmentAsync()), "alice");

        Assert.NotEmpty(cookie);
        await using var context = database.CreateContext();
        Assert.Empty(await context.DataProtectionKeys.ToListAsync());
    }

    [Fact]
    public async Task ACookieThisAppDidNotWrite_DoesNotSignAnyoneIn()
    {
        var app = await StartDevelopmentAsync();
        var client = Client(app);
        // Properly encrypted, but with a provider's own claims instead of a voter key.
        var foreign = Tickets(app).Protect(new AuthenticationTicket(
            new(new System.Security.Claims.ClaimsIdentity([new("sub", "12345"), new(SignInPrincipal.ProviderClaim, "dev")],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            CookieAuthenticationDefaults.AuthenticationScheme));

        // The right two claims, and one more: not a cookie this app writes either.
        var voterKey = Convert.ToBase64String(app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.Development, "alice"));
        var padded = Tickets(app).Protect(new AuthenticationTicket(
            new(new System.Security.Claims.ClaimsIdentity(
                [new(SignInPrincipal.VoterClaim, voterKey), new(SignInPrincipal.ProviderClaim, "dev"), new("email", "alice@example.com")],
                CookieAuthenticationDefaults.AuthenticationScheme)),
            CookieAuthenticationDefaults.AuthenticationScheme));

        Assert.False((await MeAsync(client, foreign)).GetProperty("signedIn").GetBoolean());
        Assert.False((await MeAsync(client, padded)).GetProperty("signedIn").GetBoolean());
        Assert.False((await MeAsync(client, "not-a-cookie")).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task SignIns_AreCountedPerProviderAndOutcome()
    {
        var app = await StartDevelopmentAsync();
        using var signIns = new MetricCollector<long>(app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            LazyDadTelemetry.Name, "lazydad.signins");

        await SignInAsync(Client(app), "alice");

        var counted = signIns.GetMeasurementSnapshot()
            .Where(m => m.Value > 0)
            .Select(m => ((string)m.Tags["provider"]!, (string)m.Tags["outcome"]!))
            .Order()
            .ToList();
        Assert.Equal([("dev", SignInMetrics.Completed), ("dev", SignInMetrics.Started)], counted);
    }

    private async Task<(HttpClient Client, WebApplication App, FakeGitHub GitHub)> StartWithGitHubAsync()
    {
        var gitHub = new FakeGitHub();
        var settings = Settings(Pepper, "");
        settings["SignIn:GitHub:ClientId"] = FakeGitHub.ClientId;
        settings["SignIn:GitHub:ClientSecret"] = FakeGitHub.ClientSecret;
        var app = await StartAsync(Environments.Development, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(SignInProviders.GitHub)).ConfigurePrimaryHttpMessageHandler(() => gitHub));
        return (Client(app), app, gitHub);
    }

    // Starts a sign-in with GitHub: where it sends the reader, and the correlation cookie the callback needs.
    private static async Task<(Uri Authorize, string Correlation)> StartGitHubSignInAsync(HttpClient client, string returnUrl)
    {
        using var response = await GetAsync(client, $"auth/signin/github?returnUrl={Uri.EscapeDataString(returnUrl)}", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var correlation = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        return (response.Headers.Location!, correlation.Split(';')[0]);
    }

    // GitHub sends the reader back with these query parameters.
    private static async Task<HttpResponseMessage> GitHubCallbackAsync(HttpClient client, string correlation, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"signin-github?{query}");
        request.Headers.Add("Cookie", correlation);
        return await client.SendAsync(request);
    }

    private static string State(Uri authorize) => QueryHelpers.ParseQuery(authorize.Query)["state"].ToString();

    [Fact]
    public async Task SignIn_WithGitHub_SendsTheReaderToGitHub_WithPkceAndNoScopes()
    {
        var (client, _, _) = await StartWithGitHubAsync();

        var (authorize, _) = await StartGitHubSignInAsync(client, "/");

        Assert.Equal("https://github.com/login/oauth/authorize", authorize.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(FakeGitHub.ClientId, query["client_id"].ToString());
        Assert.Equal(new Uri(client.BaseAddress!, "signin-github").AbsoluteUri, query["redirect_uri"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal("", query["scope"].ToString());
    }

    [Fact]
    public async Task SignIn_WithGitHub_KeepsOnlyTheVoterKey_AndReturnsToThePage()
    {
        var (client, app, gitHub) = await StartWithGitHubAsync();
        using var signIns = new MetricCollector<long>(app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            LazyDadTelemetry.Name, "lazydad.signins");
        using var providerCalls = new MetricCollector<double>(app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            LazyDadTelemetry.Name, "lazydad.signin.provider.duration");
        var (authorize, correlation) = await StartGitHubSignInAsync(client, "/j/5");

        using var response = await GitHubCallbackAsync(client, correlation, $"code={FakeGitHub.Code}&state={Uri.EscapeDataString(State(authorize))}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/j/5", response.Headers.Location!.OriginalString);
        Assert.Equal(FakeGitHub.Code, gitHub.TokenRequest["code"]);
        Assert.NotEmpty(gitHub.TokenRequest["code_verifier"]);

        // The account id is GitHub's numeric id; the login, name and email are dropped.
        var cookie = CookieValue(response);
        var claims = Tickets(app).Unprotect(cookie)!.Principal.Claims.Select(c => (c.Type, c.Value)).Order().ToList();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(SignInProviders.GitHub, "12345");
        Assert.Equal([(SignInPrincipal.ProviderClaim, "github"), (SignInPrincipal.VoterClaim, Convert.ToBase64String(voterKey))], claims);
        Assert.Equal("github", (await MeAsync(client, cookie)).GetProperty("provider").GetString());

        var counted = signIns.GetMeasurementSnapshot().Where(m => m.Value > 0).Select(m => (string)m.Tags["outcome"]!).Order().ToList();
        Assert.Equal([SignInMetrics.Completed, SignInMetrics.Started], counted);
        // Each call to GitHub timed: how fast the real provider answers.
        var calls = providerCalls.GetMeasurementSnapshot()
            .Where(m => (string)m.Tags["operation"]! != SignInBackchannel.Probe)
            .Select(m => ((string)m.Tags["provider"]!, (string)m.Tags["operation"]!, (string)m.Tags["outcome"]!))
            .ToList();
        Assert.Equal([("github", SignInBackchannel.Token, SignInBackchannel.Ok), ("github", SignInBackchannel.UserInfo, SignInBackchannel.Ok)], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignIn_WithGitHub_KeepsTheReadersChoiceToStaySignedIn_ThroughTheRoundTrip(bool persist)
    {
        var (client, _, _) = await StartWithGitHubAsync();
        using var start = await GetAsync(client, $"auth/signin/github?returnUrl=%2F&persist={persist}", null);
        var correlation = Assert.Single(start.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));

        using var response = await GitHubCallbackAsync(client, correlation.Split(';')[0],
            $"code={FakeGitHub.Code}&state={Uri.EscapeDataString(State(start.Headers.Location!))}");

        Assert.Equal(persist, Expires(response) is not null);
    }

    [Fact]
    public async Task TheProviders_AreListedGitHubFirst_AndTheDevelopmentAccountLast()
    {
        // Development registers its made-up account before GitHub; the dialog still lists GitHub first.
        var (_, app, _) = await StartWithGitHubAsync();

        Assert.Equal([SignInProviders.GitHub, SignInProviders.Development], app.Services.GetRequiredService<EnabledSignInProviders>().Names);
    }

    [Fact]
    public async Task GitHub_IsProbedAtStartup_AndValidWithTheRightClient()
    {
        var (_, app, _) = await StartWithGitHubAsync();

        Assert.Equal(ProviderState.Valid, await ProbedAsync(app, SignInProviders.GitHub));
        Assert.Equal(ProviderState.Off, app.Services.GetRequiredService<SignInProviderStatus>()[SignInProviders.Microsoft]);
    }

    [Fact]
    public async Task GitHub_WithAWrongSecret_IsInvalid()
    {
        var settings = Settings(Pepper, "");
        settings["SignIn:GitHub:ClientId"] = FakeGitHub.ClientId;
        settings["SignIn:GitHub:ClientSecret"] = "an-old-secret";
        var gitHub = new FakeGitHub();
        var app = await StartAsync(Environments.Development, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(SignInProviders.GitHub)).ConfigurePrimaryHttpMessageHandler(() => gitHub));

        Assert.Equal(ProviderState.Invalid, await ProbedAsync(app, SignInProviders.GitHub));
    }

    // The probe runs in the background as the app starts.
    private static async Task<ProviderState> ProbedAsync(WebApplication app, string provider)
    {
        var status = app.Services.GetRequiredService<SignInProviderStatus>();
        for (var i = 0; i < 100 && status[provider] == ProviderState.Pending; i++)
            await Task.Delay(50);
        return status[provider];
    }

    [Fact]
    public async Task SignIn_WithGitHub_WithoutAnAccountId_Fails()
    {
        var (client, _, gitHub) = await StartWithGitHubAsync();
        gitHub.User = """{"login": "alice"}""";
        var (authorize, correlation) = await StartGitHubSignInAsync(client, "/");

        using var response = await GitHubCallbackAsync(client, correlation, $"code={FakeGitHub.Code}&state={Uri.EscapeDataString(State(authorize))}");

        Assert.Equal(SignInEvents.FailedRedirect, response.Headers.Location!.OriginalString);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{SignInSetup.CookieName}=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("error=access_denied")]
    [InlineData("code=" + FakeGitHub.Code)]
    public async Task SignIn_WithGitHub_WhenTheReaderCancelsOrTheCallbackIsForged_ReturnsToThePage(string query)
    {
        // access_denied: the reader said no on GitHub's page. A callback with the right state but no correlation cookie
        // didn't start in this browser.
        var (client, _, _) = await StartWithGitHubAsync();
        var (authorize, correlation) = await StartGitHubSignInAsync(client, "/");
        var cookie = query.StartsWith("error", StringComparison.Ordinal) ? correlation : "other=1";

        using var response = await GitHubCallbackAsync(client, cookie, $"{query}&state={Uri.EscapeDataString(State(authorize))}");

        Assert.Equal(SignInEvents.FailedRedirect, response.Headers.Location!.OriginalString);
        Assert.False((await MeAsync(client, null)).GetProperty("signedIn").GetBoolean());
    }
}
