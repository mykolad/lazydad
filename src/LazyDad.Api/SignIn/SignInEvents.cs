using LazyDad.Api.Telemetry;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace LazyDad.Api.SignIn;

/// <summary>
/// What every remote provider's handler does when its callback arrives, so the cookie never holds more than
/// <see cref="SignInPrincipal"/> allows. A provider passes how to find its account id in what it sent.
/// </summary>
public static class SignInEvents
{
    /// <summary>Where a failed sign-in lands: the page, which can say so.</summary>
    public const string FailedRedirect = "/?signin=failed";

    /// <summary>
    /// Replaces the provider's user (with its account id, name, email…) by one holding only the voter key and the
    /// provider. Without an account id the sign-in fails.
    /// </summary>
    public static Task OnTicketReceived(TicketReceivedContext context, string provider, Func<ClaimsPrincipal, string?> accountId)
    {
        var services = context.HttpContext.RequestServices;
        var metrics = services.GetRequiredService<SignInMetrics>();
        var id = context.Principal is { } principal ? accountId(principal) : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            metrics.Record(provider, SignInMetrics.Failed);
            context.Response.Redirect(FailedRedirect);
            context.HandleResponse();
            return Task.CompletedTask;
        }

        // Counted as completed once the cookie is written (the cookie scheme's OnSignedIn), not here.
        context.Principal = SignInPrincipal.Create(provider, services.GetRequiredService<VoterKeys>().For(provider, id));
        return Task.CompletedTask;
    }

    /// <summary>The provider refused, the reader cancelled, or the callback didn't check out (state, correlation).</summary>
    public static Task OnRemoteFailure(RemoteFailureContext context, string provider)
    {
        context.HttpContext.RequestServices.GetRequiredService<SignInMetrics>().Record(provider, SignInMetrics.Failed);
        context.Response.Redirect(FailedRedirect);
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
