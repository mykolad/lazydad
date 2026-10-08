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
        // Production's environment: a load test runs the app as production does, only without a Key Vault key.
        var app = await StartAsync(Environments.Production, settings,
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
    public async Task InALoadTest_AnOAuthProvider_SendsReadersToTheFake(string provider, string section)
    {
        var settings = Settings(Pepper, "");
        settings["SignIn:LoadTest:Authority"] = FakeAuthority;
        settings[$"{section}:ClientId"] = "load-test";
        settings[$"{section}:ClientSecret"] = "load-test";
        var client = Client(await StartAsync(Environments.Production, settings, _ => { }));

        var (authorize, _) = await StartRemoteSignInAsync(client, provider, "/");

        Assert.Equal($"{FakeAuthority}/{provider}/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(new Uri(client.BaseAddress!, $"signin-{provider}").AbsoluteUri, QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());
    }
}
