using System.Security.Cryptography;
using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Controllers;

/// <summary>
/// Meta's data-deletion callback, which a Facebook app in Live mode must have: when a reader removes the app from their
/// Facebook account, Meta posts a signed request naming them, and their votes go, as with "Delete my votes". Meta calls it
/// server to server, so Cloudflare's bot settings must let it through (runbook). Nothing about the request is kept.
/// </summary>
[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class FacebookDeletionController : ControllerBase
{
    private readonly EnabledSignInProviders providers;
    private readonly IOptions<SignInOptions> options;
    private readonly VoterKeys voterKeys;
    private readonly IVoteRepository votes;

    public FacebookDeletionController(EnabledSignInProviders providers, IOptions<SignInOptions> options, VoterKeys voterKeys,
        IVoteRepository votes)
    {
        this.providers = providers;
        this.options = options;
        this.voterKeys = voterKeys;
        this.votes = votes;
    }

    /// <summary>
    /// Answers as Meta asks: where the reader can see the request's status (the privacy page, which says what such a
    /// request deletes) and a confirmation code. The code is random and not stored: there's nothing left to look up.
    /// </summary>
    [HttpPost("auth/facebook/deletion")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Delete([FromForm(Name = "signed_request")] string? signedRequest, CancellationToken cancellationToken)
    {
        if (!providers.Contains(SignInProviders.Facebook))
            return NotFound();
        if (!FacebookSignedRequest.TryReadUserId(signedRequest, options.Value.Facebook.ClientSecret, out var userId))
            return BadRequest();

        await votes.DeleteAllAsync(voterKeys.For(SignInProviders.Facebook, userId), cancellationToken);
        return Ok(new
        {
            url = $"{Request.Scheme}://{Request.Host}/privacy#facebook-deletion",
            confirmation_code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
        });
    }
}
