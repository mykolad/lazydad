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

    private async Task<WebApplication> StartAsync(string environment, string pepper, string keyId)
        => await StartAsync(environment, pepper, keyId, keyRingInDatabase: false);

    // keyRingInDatabase: as production keeps it (a Key Vault key id configured), but with the keys left unencrypted, so
    // the test needs no Key Vault.
    private async Task<WebApplication> StartAsync(string environment, string pepper, string keyId, bool keyRingInDatabase)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SignIn:VoterKeyPepper"] = pepper,
            ["DataProtection:KeyVaultKeyId"] = keyId,
        });
        builder.Services.AddDbContext<LazyDadDbContext>(database.Configure);
        builder.Services.AddControllers().AddApplicationPart(typeof(SignInController).Assembly);
        builder.AddSignIn();
        if (keyRingInDatabase)
            builder.Services.PostConfigure<KeyManagementOptions>(options => options.XmlEncryptor = null);
        var app = builder.Build();
        apps.Add(app);
        app.Services.GetRequiredService<SignInMetrics>();
        app.UseAuthentication();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private Task<WebApplication> StartDevelopmentAsync() => StartAsync(Environments.Development, Pepper, "");

    private Task<WebApplication> StartWithTheKeyRingInTheDatabaseAsync()
        => StartAsync(Environments.Development, Pepper, KeyId, keyRingInDatabase: true);

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
        // A session cookie: gone when the browser closes ("Keep me signed in" is #74).
        Assert.DoesNotContain("expires=", cookie);

        var me = await MeAsync(client, CookieValue(response));
        Assert.True(me.GetProperty("signedIn").GetBoolean());
        Assert.Equal("dev", me.GetProperty("provider").GetString());
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
}
