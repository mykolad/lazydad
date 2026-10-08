using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Repositories;
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

    /// <summary>The most jokes one <c>GET /me/votes</c> asks about: a feed page's worth (JokesController.MaxPageSize).</summary>
    public const int MaxVoteIds = JokesController.MaxPageSize;

    private readonly EnabledSignInProviders providers;
    private readonly SignInMetrics metrics;
    private readonly IVoteRepository votes;

    public SignInController(EnabledSignInProviders providers, SignInMetrics metrics, IVoteRepository votes)
    {
        this.providers = providers;
        this.metrics = metrics;
        this.votes = votes;
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

    /// <summary>
    /// Deletes everything stored about the signed-in reader: their votes (there's no users table), taken off the jokes'
    /// counts. No count in the answer: a retry after a commit whose answer was lost finds nothing left. Other readers see
    /// the new counts once their cached copy expires (JokeReadCache). Only from the page's own script, like sign-out.
    /// </summary>
    [HttpDelete("me/votes")]
    public async Task<IActionResult> DeleteMyVotes(CancellationToken cancellationToken)
    {
        if (await SmokeSignIn.VoterAsync(HttpContext) is not { } voterKey)
            return Unauthorized();
        if (Request.Headers[RequestHeader] != RequestHeaderValue)
            return StatusCode(StatusCodes.Status403Forbidden);
        await votes.DeleteAllAsync(voterKey, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// The signed-in reader's votes on these jokes (<c>ids</c>, comma-separated, at most <see cref="MaxVoteIds"/>, as on
    /// one feed page): <c>{"&lt;id&gt;": 1 | -1}</c>, jokes without a vote left out. Always from the database, never the
    /// read cache: a reader must see their own vote at once.
    /// </summary>
    [HttpGet("me/votes")]
    public async Task<IActionResult> MyVotes([FromQuery] string? ids, CancellationToken cancellationToken)
    {
        if (await SmokeSignIn.VoterAsync(HttpContext) is not { } voterKey)
            return Unauthorized();
        var jokeIds = new HashSet<int>();
        foreach (var part in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
                return BadRequest("ids must be joke ids, comma-separated.");
            jokeIds.Add(id);
        }
        if (jokeIds.Count > MaxVoteIds)
            return BadRequest($"At most {MaxVoteIds} ids.");
        var found = jokeIds.Count == 0 ? new Dictionary<int, int>() : await votes.GetAsync(voterKey, jokeIds, cancellationToken);
        return Ok(found.ToDictionary(vote => vote.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), vote => vote.Value));
    }
}
