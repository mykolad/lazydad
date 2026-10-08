using LazyDad.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace LazyDad.Api.SignIn;

/// <summary>
/// How the smoke tests vote signed in without a reader's account: providers block scripted logins, and test accounts
/// would need passwords kept somewhere. The runner already signs in to Azure as the environment's deploy identity (OIDC,
/// no secret), so the app trusts exactly that identity's Entra token for the <c>lazydad-smoke</c> registration, as one fixed voter,
/// and only where a signed-in reader's votes are (<see cref="VoterAsync"/>): every other endpoint ignores it.
/// </summary>
public static class SmokeSignIn
{
    /// <summary>The smoke voter's account: one voter key per environment (each has its own pepper).</summary>
    public const string AccountId = "deploy";

    public static void Add(AuthenticationBuilder authentication, SmokeSignInOptions smoke)
    {
        // As Entra writes it in a token's issuer (lowercase), whatever case the setting has; issuers compare as strings.
        var tenant = Guid.Parse(smoke.TenantId).ToString("D");
        authentication.AddJwtBearer(SignInProviders.Smoke, jwt =>
        {
            jwt.Authority = $"https://login.microsoftonline.com/{tenant}/v2.0";
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.ValidAudience = smoke.Audience;
            // An audience-only registration gets v1 tokens unless it asks for v2; either is the same tenant's.
            jwt.TokenValidationParameters.ValidIssuers =
                [$"https://sts.windows.net/{tenant}/", $"https://login.microsoftonline.com/{tenant}/v2.0"];
            jwt.Events = new JwtBearerEvents
            {
                // Anyone in the tenant can get a token for the audience; only the deploy identity counts.
                OnTokenValidated = context =>
                {
                    var objectId = context.Principal?.FindFirst("oid")?.Value;
                    if (!string.Equals(context.Principal?.FindFirst("tid")?.Value, tenant, StringComparison.OrdinalIgnoreCase)
                        || objectId is null || !smoke.AllowedObjectIds.Contains(objectId, StringComparer.OrdinalIgnoreCase))
                        context.Fail("Not an identity the smoke sign-in trusts.");
                    return Task.CompletedTask;
                },
            };
        });
    }

    /// <summary>
    /// The voter for an endpoint about the signed-in reader's votes: the reader of the sign-in cookie, else the smoke
    /// voter for a valid smoke token, else null.
    /// </summary>
    public static async Task<byte[]?> VoterAsync(HttpContext context)
    {
        if (SignInPrincipal.TryRead(context.User, out var voterKey, out _))
            return voterKey;
        var schemes = context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetSchemeAsync(SignInProviders.Smoke) is null)
            return null;
        var result = await context.AuthenticateAsync(SignInProviders.Smoke);
        return result.Succeeded
            ? context.RequestServices.GetRequiredService<VoterKeys>().For(SignInProviders.Smoke, AccountId)
            : null;
    }
}
