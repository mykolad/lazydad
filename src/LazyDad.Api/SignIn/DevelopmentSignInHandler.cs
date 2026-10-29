using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.SignIn;

/// <summary>
/// The Development provider, an authentication scheme like every remote one: its challenge signs the reader in at once,
/// as a made-up account (<c>?account=</c> on the sign-in URL picks it, to vote as several readers), and returns to the
/// page. Registered only when the environment is Development and sign-in is on (see SignInSetup).
/// </summary>
public sealed class DevelopmentSignInHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The account when the sign-in URL names none.</summary>
    public const string DefaultAccount = "dev";

    private readonly VoterKeys voterKeys;

    public DevelopmentSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        VoterKeys voterKeys)
        : base(options, logger, encoder)
    {
        this.voterKeys = voterKeys;
    }

    // It only starts sign-ins: the cookie scheme reads who's signed in.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var account = Request.Query["account"].ToString();
        var principal = SignInPrincipal.Create(Scheme.Name,
            voterKeys.For(Scheme.Name, string.IsNullOrWhiteSpace(account) ? DefaultAccount : account));
        await Context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = properties.IsPersistent });
        Response.Redirect(properties.RedirectUri ?? "/");
    }
}
