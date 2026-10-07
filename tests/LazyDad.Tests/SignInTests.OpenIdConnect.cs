using System.Net;
using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;

namespace LazyDad.Tests;

/// <summary>The OpenID Connect providers, each against a fake of itself (<see cref="FakeOpenIdProvider"/>).</summary>
public sealed partial class SignInTests
{
    // Each OpenID Connect provider: its settings section and its endpoints.
    private static readonly Dictionary<string, (string Section, OpenIdProvider Endpoints)> OpenIdProviderSettings = new()
    {
        [SignInProviders.Google] = ("Google", OpenIdProviders.Google),
    };

    private async Task<(HttpClient Client, WebApplication App, FakeOpenIdProvider Provider)> StartWithOpenIdProviderAsync(string provider,
        string clientSecret)
    {
        var (section, endpoints) = OpenIdProviderSettings[provider];
        var fake = new FakeOpenIdProvider(endpoints);
        var settings = Settings(Pepper, "");
        settings[$"SignIn:{section}:ClientId"] = FakeOpenIdProvider.ClientId;
        settings[$"SignIn:{section}:ClientSecret"] = clientSecret;
        var app = await StartAsync(Environments.Development, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(provider)).ConfigurePrimaryHttpMessageHandler(() => fake));
        return (Client(app), app, fake);
    }

    private Task<(HttpClient Client, WebApplication App, FakeOpenIdProvider Provider)> StartWithOpenIdProviderAsync(string provider)
        => StartWithOpenIdProviderAsync(provider, FakeOpenIdProvider.ClientSecret);

    // Starts a sign-in: where it sends the reader, and the cookies the callback needs (the correlation and the nonce).
    private static async Task<(Uri Authorize, string Cookies)> StartRemoteSignInAsync(HttpClient client, string provider, string returnUrl)
    {
        using var response = await GetAsync(client, $"auth/signin/{provider}?returnUrl={Uri.EscapeDataString(returnUrl)}", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookies = response.Headers.GetValues("Set-Cookie")
            .Where(c => c.StartsWith(".AspNetCore.", StringComparison.Ordinal))
            .Select(c => c.Split(';')[0]);
        return (response.Headers.Location!, string.Join("; ", cookies));
    }

    // The provider sends the reader back to /signin-<provider> with these query parameters.
    private static async Task<HttpResponseMessage> ProviderCallbackAsync(HttpClient client, string provider, string cookies, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"signin-{provider}?{query}");
        request.Headers.Add("Cookie", cookies);
        return await client.SendAsync(request);
    }

    // A whole sign-in: the start, the provider issuing its id token for the nonce it was given, and the callback.
    private static async Task<HttpResponseMessage> SignInThroughAsync(HttpClient client, string provider, FakeOpenIdProvider fake, string returnUrl)
    {
        var (authorize, cookies) = await StartRemoteSignInAsync(client, provider, returnUrl);
        var query = QueryHelpers.ParseQuery(authorize.Query);
        fake.Nonce ??= query["nonce"].ToString();
        return await ProviderCallbackAsync(client, provider, cookies,
            $"code={FakeOpenIdProvider.Code}&state={Uri.EscapeDataString(query["state"].ToString())}");
    }

    private static bool SignedIn(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith($"{SignInSetup.CookieName}=", StringComparison.Ordinal) && !c.StartsWith($"{SignInSetup.CookieName}=;", StringComparison.Ordinal));

    public static TheoryData<string> OpenIdProviderNames() => new(OpenIdProviderSettings.Keys);

    [Theory]
    [MemberData(nameof(OpenIdProviderNames))]
    public async Task SignIn_WithAnOpenIdProvider_SendsTheReaderToIt_WithPkceAndTheOpenidScopeOnly(string provider)
    {
        var (client, _, _) = await StartWithOpenIdProviderAsync(provider);

        var (authorize, _) = await StartRemoteSignInAsync(client, provider, "/");

        Assert.Equal(OpenIdProviderSettings[provider].Endpoints.AuthorizeEndpoint, authorize.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(FakeOpenIdProvider.ClientId, query["client_id"].ToString());
        Assert.Equal(new Uri(client.BaseAddress!, $"signin-{provider}").AbsoluteUri, query["redirect_uri"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        // The answer comes back in the query, the code flow's default (so the handler sends no response_mode), not as a
        // cross-site form post.
        Assert.False(query.ContainsKey("response_mode"));
        // No email, no profile.
        Assert.Equal("openid", query["scope"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.NotEmpty(query["nonce"].ToString());
    }

    [Theory]
    [MemberData(nameof(OpenIdProviderNames))]
    public async Task SignIn_WithAnOpenIdProvider_KeepsOnlyTheVoterKey_AndReturnsToThePage(string provider)
    {
        var (client, app, fake) = await StartWithOpenIdProviderAsync(provider);
        using var providerCalls = new MetricCollector<double>(app.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            LazyDadTelemetry.Name, "lazydad.signin.provider.duration");

        using var response = await SignInThroughAsync(client, provider, fake, "/j/5");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/j/5", response.Headers.Location!.OriginalString);
        Assert.NotEmpty(fake.TokenRequest["code_verifier"]);
        // The account id is the id token's sub; its name and email are dropped.
        var claims = Tickets(app).Unprotect(CookieValue(response))!.Principal.Claims.Select(c => (c.Type, c.Value)).Order().ToList();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(provider, fake.Subject!);
        Assert.Equal([(SignInPrincipal.ProviderClaim, provider), (SignInPrincipal.VoterClaim, Convert.ToBase64String(voterKey))], claims);
        Assert.Contains(providerCalls.GetMeasurementSnapshot(), m => (string)m.Tags["operation"]! == SignInBackchannel.Token
            && (string)m.Tags["outcome"]! == SignInBackchannel.Ok);
    }

    public static TheoryData<string, string> ForgedIdTokens()
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in OpenIdProviderSettings.Keys)
            foreach (var forgery in new[] { "issuer", "audience", "signature", "nonce", "subject" })
                data.Add(provider, forgery);
        return data;
    }

    [Theory]
    [MemberData(nameof(ForgedIdTokens))]
    public async Task SignIn_WithAnOpenIdProvider_RefusesAnIdTokenThatDoesNotCheckOut(string provider, string forgery)
    {
        var (client, _, fake) = await StartWithOpenIdProviderAsync(provider);
        switch (forgery)
        {
            case "issuer": fake.Issuer = "https://evil.example"; break;
            case "audience": fake.Audience = "another-app"; break;
            case "signature": fake.SignWithAnotherKey = true; break;
            case "nonce": fake.Nonce = "another-sign-in"; break;
            case "subject": fake.Subject = null; break;
        }

        using var response = await SignInThroughAsync(client, provider, fake, "/");

        Assert.Equal(SignInEvents.FailedRedirect, response.Headers.Location!.OriginalString);
        Assert.False(SignedIn(response));
    }

    [Theory]
    [MemberData(nameof(OpenIdProviderNames))]
    public async Task AnOpenIdProvider_IsProbed_ValidWithTheRightClient_InvalidWithAWrongSecret(string provider)
    {
        var (_, right, _) = await StartWithOpenIdProviderAsync(provider);
        var (_, wrong, _) = await StartWithOpenIdProviderAsync(provider, "an-old-secret");

        Assert.Equal(ProviderState.Valid, await ProbedAsync(right, provider));
        Assert.Equal(ProviderState.Invalid, await ProbedAsync(wrong, provider));
    }

    [Theory]
    [MemberData(nameof(OpenIdProviderNames))]
    public async Task AnOpenIdProvider_HasNoRemoteSignOut_ThatAnotherSiteCouldUse(string provider)
    {
        // Provider-initiated sign-out would sign the reader out on any site's request, around the site's X-LazyDad check.
        var (client, app, fake) = await StartWithOpenIdProviderAsync(provider);
        using var signIn = await SignInThroughAsync(client, provider, fake, "/");
        var cookie = CookieValue(signIn);

        foreach (var path in new[] { $"signout-{provider}", "signout-oidc", $"signout-callback-{provider}", "signout-callback-oidc" })
        {
            using var response = await GetAsync(client, path, cookie);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
                && cookies.Any(c => c.StartsWith($"{SignInSetup.CookieName}=;", StringComparison.Ordinal)), $"/{path} signed the reader out.");
        }
        Assert.True((await MeAsync(client, cookie)).GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task Google_AcceptsItsIssuerWithoutTheScheme()
    {
        // Google's documentation: an id token's iss is https://accounts.google.com or accounts.google.com.
        var (client, _, fake) = await StartWithOpenIdProviderAsync(SignInProviders.Google);
        fake.Issuer = "accounts.google.com";

        using var response = await SignInThroughAsync(client, SignInProviders.Google, fake, "/");

        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.True(SignedIn(response));
    }
}
