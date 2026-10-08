using System.Net;
using LazyDad.Api.SignIn;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LazyDad.Tests;

/// <summary>
/// The load test's setting (<c>SignIn:LoadTest:Authority</c>): every provider at the fake identity provider's address,
/// with the same checks as the real ones, and the key ring in the (throwaway) database without a Key Vault key.
/// </summary>
public sealed partial class SignInTests
{
    private const string FakeAuthority = "https://idp.loadtest.test";

    [Theory]
    [InlineData(SignInProviders.Google)]
    [InlineData(SignInProviders.Microsoft)]
    [InlineData(SignInProviders.Telegram)]
    public async Task InALoadTest_AnOpenIdProvider_IsTheFake_WithTheRealChecks(string provider)
    {
        var endpoints = ProviderEndpoints.For(FakeAuthority);
        var fake = new FakeOpenIdProvider(provider switch
        {
            SignInProviders.Google => endpoints.Google,
            SignInProviders.Microsoft => endpoints.Microsoft,
            _ => endpoints.Telegram,
        });
        OpenIdProviderSettings[provider].Prepare(fake);
        if (provider == SignInProviders.Microsoft)
            fake.ClientCredentialsEndpoint = endpoints.MicrosoftTenantToken(MicrosoftTenant);
        var (section, _, extra, _) = OpenIdProviderSettings[provider];
        var settings = Settings(Pepper, "");
        settings["SignIn:LoadTest:Authority"] = FakeAuthority;
        settings[$"SignIn:{section}:ClientId"] = FakeOpenIdProvider.ClientId;
        settings[$"SignIn:{section}:ClientSecret"] = FakeOpenIdProvider.ClientSecret;
        foreach (var (key, value) in extra)
            settings[key] = value;
        // The load test's own environment: as production otherwise, only without a Key Vault key.
        var app = await StartAsync(LazyDad.Api.Configuration.SignInOptionsValidator.LoadTestEnvironment, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(provider)).ConfigurePrimaryHttpMessageHandler(() => fake));
        var client = Client(app);

        var (authorize, _) = await StartRemoteSignInAsync(client, provider, "/");
        using var response = await SignInThroughAsync(client, provider, fake, "/");

        Assert.StartsWith($"{FakeAuthority}/{provider}/authorize", authorize.AbsoluteUri);
        Assert.True(SignedIn(response));
        Assert.Equal(ProviderState.Valid, await ProbedAsync(app, provider));
        // The replicas share the ring through the load test's database.
        await using var context = database.CreateContext();
        Assert.NotEmpty(await context.DataProtectionKeys.ToListAsync());
    }

    [Theory]
    [InlineData(SignInProviders.GitHub, "SignIn:GitHub")]
    [InlineData(SignInProviders.Facebook, "SignIn:Facebook")]
    public async Task InALoadTest_AnOAuthProvider_IsTheFake_FromTheRedirectToTheCookie(string provider, string section)
    {
        var fake = new FakeLoadTestOAuth(provider);
        var settings = Settings(Pepper, "");
        settings["SignIn:LoadTest:Authority"] = FakeAuthority;
        settings[$"{section}:ClientId"] = "load-test";
        settings[$"{section}:ClientSecret"] = "load-test";
        var app = await StartAsync(LazyDad.Api.Configuration.SignInOptionsValidator.LoadTestEnvironment, settings,
            services => services.AddHttpClient(SignInBackchannel.ClientName(provider)).ConfigurePrimaryHttpMessageHandler(() => fake));
        var client = Client(app);

        var (authorize, cookies) = await StartRemoteSignInAsync(client, provider, "/j/3");
        using var response = await ProviderCallbackAsync(client, provider, cookies,
            $"code={FakeLoadTestOAuth.Code}&state={Uri.EscapeDataString(QueryHelpers.ParseQuery(authorize.Query)["state"].ToString())}");

        Assert.Equal($"{FakeAuthority}/{provider}/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(new Uri(client.BaseAddress!, $"signin-{provider}").AbsoluteUri, QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());
        Assert.Equal("/j/3", response.Headers.Location!.OriginalString);
        var claims = Tickets(app).Unprotect(CookieValue(response))!.Principal.Claims.Select(c => (c.Type, c.Value)).Order().ToList();
        var voterKey = app.Services.GetRequiredService<VoterKeys>().For(provider, FakeLoadTestOAuth.Account);
        Assert.Equal([(SignInPrincipal.ProviderClaim, provider), (SignInPrincipal.VoterClaim, Convert.ToBase64String(voterKey))], claims);
        Assert.Equal(ProviderState.Valid, await ProbedAsync(app, provider));
    }

    /// <summary>
    /// GitHub's or Facebook's endpoints where the load test's fake has them (<c>&lt;authority&gt;/&lt;provider&gt;/token</c>
    /// and <c>/user</c>), answering as tests/load/FakeIdentityProvider.cs does.
    /// </summary>
    private sealed class FakeLoadTestOAuth(string provider) : HttpMessageHandler
    {
        public const string Code = "load-test-code";
        public const string Account = "4242";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (request.Method == HttpMethod.Post && path == $"{FakeAuthority}/{provider}/token")
            {
                var form = QueryHelpers.ParseQuery("?" + await request.Content!.ReadAsStringAsync(cancellationToken));
                if (form["grant_type"] == "client_credentials")
                    return Json(HttpStatusCode.OK, """{"access_token": "app-token", "token_type": "bearer"}""");
                return form["code"] == Code
                    ? Json(HttpStatusCode.OK, """{"access_token": "user-token", "token_type": "bearer"}""")
                    : Json(HttpStatusCode.OK, """{"error": "bad_verification_code"}""");
            }
            if (request.Method == HttpMethod.Get && path == $"{FakeAuthority}/{provider}/user")
            {
                var token = provider == SignInProviders.GitHub
                    ? request.Headers.Authorization?.Parameter
                    : QueryHelpers.ParseQuery(request.RequestUri.Query)["access_token"].ToString();
                return token == "user-token"
                    ? Json(HttpStatusCode.OK, provider == SignInProviders.GitHub ? $$"""{"id": {{Account}}}""" : $$"""{"id": "{{Account}}"}""")
                    : Json(HttpStatusCode.Unauthorized, "{}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
