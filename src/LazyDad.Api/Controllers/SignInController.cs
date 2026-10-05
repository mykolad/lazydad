using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace LazyDad.Api.Controllers;

/// <summary>
/// Signing in and out, and who's signed in. Nothing here is cached anywhere: the answers belong to one reader.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SignInController : ControllerBase
{
    /// <summary>The Development provider's account when none is given (<c>?account=</c> picks another, to vote as several readers).</summary>
    public const string DevelopmentAccount = "dev";

    private readonly EnabledSignInProviders providers;
    private readonly VoterKeys voterKeys;
    private readonly SignInMetrics metrics;

    public SignInController(EnabledSignInProviders providers, VoterKeys voterKeys, SignInMetrics metrics)
    {
        this.providers = providers;
        this.voterKeys = voterKeys;
        this.metrics = metrics;
    }

    /// <summary>
    /// Sends the reader to the provider, who sends them back to <paramref name="returnUrl"/> signed in. Only a URL on
    /// this site is followed; anything else goes to the home page.
    /// </summary>
    [HttpGet("auth/signin/{provider}")]
    public async Task<IActionResult> StartSignIn(string provider, [FromQuery] string? returnUrl, [FromQuery] string? account)
    {
        if (!providers.Contains(provider))
            return NotFound();
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        metrics.Record(provider, SignInMetrics.Started);

        if (provider == SignInProviders.Development)
        {
            var principal = SignInPrincipal.Create(provider,
                voterKeys.For(provider, string.IsNullOrWhiteSpace(account) ? DevelopmentAccount : account));
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            metrics.Record(provider, SignInMetrics.Completed);
            return LocalRedirect(target);
        }

        // A remote provider: its handler (named after it) takes over, and its callback signs the reader in (SignInEvents).
        return Challenge(new AuthenticationProperties { RedirectUri = target }, provider);
    }

    [HttpPost("auth/signout")]
    public async Task<IActionResult> SignOutOfSite()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    /// <summary>Whether the reader is signed in, and with which provider. Never anything that identifies them.</summary>
    [HttpGet("me")]
    public IActionResult Me()
        => SignInPrincipal.TryRead(User, out _, out var provider)
            ? Ok(new { signedIn = true, provider })
            : Ok(new { signedIn = false, provider = (string?)null });
}
