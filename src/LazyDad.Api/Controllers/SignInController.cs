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
    /// <summary>The header the page's own requests that change something send (see <see cref="SignOutOfSite"/>).</summary>
    public const string RequestHeader = "X-LazyDad";
    public const string RequestHeaderValue = "1";

    private readonly EnabledSignInProviders providers;
    private readonly SignInMetrics metrics;

    public SignInController(EnabledSignInProviders providers, SignInMetrics metrics)
    {
        this.providers = providers;
        this.metrics = metrics;
    }

    /// <summary>
    /// Sends the reader to the provider, who sends them back to <paramref name="returnUrl"/> signed in. Only a URL on
    /// this site is followed; anything else goes to the home page. <paramref name="persist"/>: the reader ticked "Keep me
    /// signed in" (see <see cref="SignInSetup.Lifetime"/>); the choice travels to the callback inside the sign-in's state.
    /// </summary>
    [HttpGet("auth/signin/{provider}")]
    public IActionResult StartSignIn(string provider, [FromQuery] string? returnUrl, [FromQuery] bool persist)
    {
        if (!providers.Contains(provider))
            return NotFound();
        var target = Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        metrics.Record(provider, SignInMetrics.Started);

        // Every provider is an authentication scheme named after it, and its handler takes over: a remote one sends the
        // reader to the provider, whose callback signs them in (SignInEvents); Development's signs them in at once.
        return Challenge(new AuthenticationProperties { RedirectUri = target, IsPersistent = persist }, provider);
    }

    /// <summary>
    /// Only from the page's own script, which sends <see cref="RequestHeader"/>: another site's form can't add a header,
    /// and its script would need a CORS permission this site never gives. Without the check, any site could sign a
    /// reader out (the reader's cookie isn't sent cross-site, but the answer's expired cookie would still replace it).
    /// </summary>
    [HttpPost("auth/signout")]
    public async Task<IActionResult> SignOutOfSite()
    {
        if (Request.Headers[RequestHeader] != RequestHeaderValue)
            return StatusCode(StatusCodes.Status403Forbidden);
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
