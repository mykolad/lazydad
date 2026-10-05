using System.Diagnostics.Metrics;
using System.Security.Claims;
using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

/// <summary>What every remote provider's callback does (SignInEvents), as a provider's handler would call it.</summary>
public sealed class SignInEventsTests : IDisposable
{
    private static readonly string Pepper = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly AuthenticationScheme Scheme = new(SignInProviders.Microsoft, null, typeof(CookieAuthenticationHandler));

    private readonly ServiceProvider services;
    private readonly MetricCollector<long> signIns;

    public SignInEventsTests()
    {
        services = new ServiceCollection()
            .AddMetrics()
            .AddSingleton(Options.Create(new SignInOptions { VoterKeyPepper = Pepper }))
            .AddSingleton<VoterKeys>()
            .AddSingleton(new EnabledSignInProviders([SignInProviders.Microsoft]))
            .AddSingleton<SignInMetrics>()
            .BuildServiceProvider();
        services.GetRequiredService<SignInMetrics>();
        signIns = new MetricCollector<long>(services.GetRequiredService<IMeterFactory>(), LazyDadTelemetry.Name, "lazydad.signins");
    }

    public void Dispose()
    {
        signIns.Dispose();
        services.Dispose();
    }

    private DefaultHttpContext HttpContext() => new() { RequestServices = services };

    private string[] Counted() => signIns.GetMeasurementSnapshot().Where(m => m.Value > 0).Select(m => (string)m.Tags["outcome"]!).ToArray();

    private static TicketReceivedContext TicketReceived(HttpContext http, params Claim[] claims)
        => new(http, Scheme, new RemoteAuthenticationOptions(),
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "provider")), Scheme.Name));

    [Fact]
    public async Task OnTicketReceived_KeepsOnlyTheVoterKeyAndTheProvider()
    {
        var context = TicketReceived(HttpContext(), new("sub", "12345"), new("name", "Alice Example"), new("email", "alice@example.com"));

        await SignInEvents.OnTicketReceived(context, SignInProviders.Microsoft, p => p.FindFirst("sub")?.Value);

        var expectedKey = services.GetRequiredService<VoterKeys>().For(SignInProviders.Microsoft, "12345");
        Assert.Equal(
            [(SignInPrincipal.VoterClaim, Convert.ToBase64String(expectedKey)), (SignInPrincipal.ProviderClaim, SignInProviders.Microsoft)],
            context.Principal!.Claims.Select(c => (c.Type, c.Value)));
        Assert.Null(context.Result);
        Assert.Equal([SignInMetrics.Completed], Counted());
    }

    [Fact]
    public async Task OnTicketReceived_WithoutAnAccountId_FailsTheSignIn()
    {
        var http = HttpContext();
        var context = TicketReceived(http, new Claim("name", "Alice Example"));

        await SignInEvents.OnTicketReceived(context, SignInProviders.Microsoft, p => p.FindFirst("sub")?.Value);

        Assert.True(context.Result!.Handled);
        Assert.Equal(SignInEvents.FailedRedirect, http.Response.Headers.Location.ToString());
        Assert.Equal([SignInMetrics.Failed], Counted());
    }

    [Fact]
    public async Task OnRemoteFailure_SendsTheReaderBackToThePage()
    {
        var http = HttpContext();
        var context = new RemoteFailureContext(http, Scheme, new RemoteAuthenticationOptions(), new AuthenticationFailureException("Correlation failed."));

        await SignInEvents.OnRemoteFailure(context, SignInProviders.Microsoft);

        Assert.True(context.Result!.Handled);
        Assert.Equal(SignInEvents.FailedRedirect, http.Response.Headers.Location.ToString());
        Assert.Equal([SignInMetrics.Failed], Counted());
    }

    [Fact]
    public void TryRead_RejectsAnythingButAVoterKeyAndAKnownProvider()
    {
        var key = new byte[Data.Entities.Vote.VoterKeyLength];
        Assert.True(SignInPrincipal.TryRead(SignInPrincipal.Create(SignInProviders.GitHub, key), out var read, out var provider));
        Assert.Equal(key, read);
        Assert.Equal(SignInProviders.GitHub, provider);

        Assert.False(SignInPrincipal.TryRead(SignInPrincipal.Create("x", key), out _, out _));
        Assert.False(SignInPrincipal.TryRead(SignInPrincipal.Create(SignInProviders.GitHub, new byte[16]), out _, out _));
        Assert.False(SignInPrincipal.TryRead(new ClaimsPrincipal(new ClaimsIdentity(SignInPrincipal.Create(SignInProviders.GitHub, key).Claims)), out _, out _));
        Assert.False(SignInPrincipal.TryRead(null, out _, out _));
    }
}
