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
    private const string MicrosoftTenant = "3f1a6c2e-5b7d-4e8f-9a0b-1c2d3e4f5a6b";

    // Each OpenID Connect provider: its settings section and endpoints, any settings it needs besides its client, and how
    // its fake answers like it.
    private static readonly Dictionary<string, (string Section, OpenIdProvider Endpoints, Dictionary<string, string?> Settings,
        Action<FakeOpenIdProvider> Prepare)> OpenIdProviderSettings = new()
    {
        [SignInProviders.Google] = ("Google", OpenIdProviders.Google, [], _ => { }),
        // Each token names its own tenant as the issuer; the probe asks the registration's tenant for an app token.
        [SignInProviders.Microsoft] = ("Microsoft", OpenIdProviders.Microsoft, new() { ["SignIn:Microsoft:TenantId"] = MicrosoftTenant }, fake =>
        {
            fake.Issuer = $"https://login.microsoftonline.com/{MicrosoftTenant}/v2.0";
            fake.ExtraClaims["tid"] = MicrosoftTenant;
            fake.ClientCredentialsEndpoint = OpenIdProviders.MicrosoftTenantTokenEndpoint(MicrosoftTenant);
        }),
    };

    private async Task<(HttpClient Client, WebApplication App, FakeOpenIdProvider Provider)> StartWithOpenIdProviderAsync(string provider,
        Action<Dictionary<string, string?>> settings, Action<IServiceCollection> configure)
    {
        var (section, endpoints, extra, prepare) = OpenIdProviderSettings[provider];
        var fake = new FakeOpenIdProvider(endpoints);
        prepare(fake);
        var all = Settings(Pepper, "");
        all[$"SignIn:{section}:ClientId"] = FakeOpenIdProvider.ClientId;
        all[$"SignIn:{section}:ClientSecret"] = FakeOpenIdProvider.ClientSecret;
        foreach (var (key, value) in extra)
            all[key] = value;
        settings(all);
        var app = await StartAsync(Environments.Development, all, services =>
        {
            services.AddHttpClient(SignInBackchannel.ClientName(provider)).ConfigurePrimaryHttpMessageHandler(() => fake);
            configure(services);
        });
        return (Client(app), app, fake);
    }

    private Task<(HttpClient Client, WebApplication App, FakeOpenIdProvider Provider)> StartWithOpenIdProviderAsync(string provider,
        string clientSecret)
        => StartWithOpenIdProviderAsync(provider, settings => settings[$"SignIn:{OpenIdProviderSettings[provider].Section}:ClientSecret"] = clientSecret, _ => { });

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

    [Fact]
    public async Task Microsoft_RefusesAnIssuerThatIsNotTheTokensOwnTenant()
    {
        // A token from one tenant claiming another's issuer: with "common", any tenant's tokens are welcome, but only as itself.
        var (client, _, fake) = await StartWithOpenIdProviderAsync(SignInProviders.Microsoft);
        fake.Issuer = "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0";

        using var response = await SignInThroughAsync(client, SignInProviders.Microsoft, fake, "/");

        Assert.Equal(SignInEvents.FailedRedirect, response.Headers.Location!.OriginalString);
        Assert.False(SignedIn(response));
    }

    private sealed class FakeManagedIdentity(string token) : IClientAssertion
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    // Microsoft with no secret: the app registration trusts the managed identity, whose token it sends instead.
    private Task<(HttpClient Client, WebApplication App, FakeOpenIdProvider Provider)> StartWithMicrosoftAndAManagedIdentityAsync(string token)
        => StartWithOpenIdProviderAsync(SignInProviders.Microsoft,
            settings =>
            {
                settings["SignIn:Microsoft:ClientSecret"] = "";
                settings["SignIn:Microsoft:ManagedIdentityClientId"] = "8d0f1c7e-0000-4000-8000-000000000001";
            },
            services => services.AddSingleton<IClientAssertion>(new FakeManagedIdentity(token)));

    [Fact]
    public async Task Microsoft_WithAManagedIdentity_SendsItsTokenInsteadOfASecret()
    {
        var (client, app, fake) = await StartWithMicrosoftAndAManagedIdentityAsync(FakeOpenIdProvider.Assertion);

        using var response = await SignInThroughAsync(client, SignInProviders.Microsoft, fake, "/");

        Assert.True(SignedIn(response));
        Assert.Equal(FakeOpenIdProvider.Assertion, fake.TokenRequest["client_assertion"]);
        Assert.Equal(IClientAssertion.Type, fake.TokenRequest["client_assertion_type"]);
        Assert.False(fake.TokenRequest.ContainsKey("client_secret"));
        Assert.Equal(ProviderState.Valid, await ProbedAsync(app, SignInProviders.Microsoft));
    }

    [Fact]
    public async Task Microsoft_WithAManagedIdentityTheRegistrationDoesNotTrust_IsInvalid()
    {
        var (_, app, _) = await StartWithMicrosoftAndAManagedIdentityAsync("another-identity's-token");

        Assert.Equal(ProviderState.Invalid, await ProbedAsync(app, SignInProviders.Microsoft));
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
