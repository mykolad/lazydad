using Azure.Identity;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using LazyDad.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Sign-in for voting (#24, issues #68–#83; <c>docs/design/sign-in-2026-10.md</c>): the cookie, its key ring, and the
/// providers. Off while <c>SignIn:VoterKeyPepper</c> is empty; then nobody can sign in, and <c>/me</c> says so.
/// </summary>
public static class SignInSetup
{
    /// <summary>Both production apps and every restart must share it, or a cookie from one can't be opened by another.</summary>
    public const string ApplicationName = "lazydad";

    /// <summary><c>__Host-</c>: the browser only accepts it Secure, for the whole site, and from this host alone.</summary>
    public const string CookieName = "__Host-lazydad";

    /// <summary>How long a sign-in lasts, renewed while it's used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    public static void AddSignIn(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<SignInOptions>()
            .Bind(builder.Configuration.GetSection(SignInOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SignInOptions>, SignInOptionsValidator>();
        services.Configure<KeyRingOptions>(builder.Configuration.GetSection(KeyRingOptions.SectionName));
        services.AddSingleton<VoterKeys>();

        services.AddSingleton<EnabledSignInProviders>();
        services.AddSingleton<SignInMetrics>();
        services.AddSingleton<KeyRingCheck>();

        // With a Key Vault key, the key ring lives in the database, on its read-write connection (never a read-only
        // replica: a key is used as soon as it's made), read about once a day, and each key is encrypted with the Key
        // Vault key. The validator requires that key wherever sign-in is on, except in Development. Without it (a local
        // run), the ring stays on this machine (ASP.NET's default): a local run often points at production's database,
        // and must never add an unencrypted key to production's ring, nor need production's Key Vault key.
        var dataProtection = services.AddDataProtection().SetApplicationName(ApplicationName);
        var keyRing = builder.Configuration.GetSection(KeyRingOptions.SectionName).Get<KeyRingOptions>() ?? new KeyRingOptions();
        if (keyRing.TryGetKeyId(out var keyId))
            dataProtection
                .PersistKeysToDbContext<LazyDadDbContext>()
                .ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential());

        var authentication = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax: sent when a reader follows a link here (and on the provider's redirect back), never with another
                // site's POST.
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = Lifetime;
                cookie.SlidingExpiration = true;
                // An API answers 401/403; there's no sign-in page to send anyone to.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                // Only a cookie this app could have written counts: sign-in on, and exactly a voter key and a provider.
                cookie.Events.OnValidatePrincipal = context =>
                {
                    if (!context.HttpContext.RequestServices.GetRequiredService<VoterKeys>().Enabled
                        || !SignInPrincipal.TryRead(context.Principal, out _, out _))
                        context.RejectPrincipal();
                    return Task.CompletedTask;
                };
                // A sign-in is complete once its cookie is written: for the dev provider and every remote one alike.
                cookie.Events.OnSignedIn = context =>
                {
                    if (SignInPrincipal.TryRead(context.Principal, out _, out var provider))
                        context.HttpContext.RequestServices.GetRequiredService<SignInMetrics>().Record(provider, SignInMetrics.Completed);
                    return Task.CompletedTask;
                };
            });

        // Each provider is an authentication scheme named after it (EnabledSignInProviders lists them), registered only
        // while sign-in is on and the provider is configured. The rest come with their issues (#70, #76, #78, #79);
        // Development's signs in at once, only in Development.
        var options = builder.Configuration.GetSection(SignInOptions.SectionName).Get<SignInOptions>() ?? new SignInOptions();
        if (!options.Enabled)
            return;
        if (builder.Environment.IsDevelopment())
            authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentSignInHandler>(SignInProviders.Development, null);
        if (options.GitHub.Configured)
            authentication.AddGitHub(SignInProviders.GitHub, github =>
            {
                Remote(github, SignInProviders.GitHub, options.GitHub);
                // No scopes: the public profile is enough, and its numeric id is the account id (never the login, which
                // the reader can change). GitHub ids are public, which is why the voter key is keyed with the pepper.
                github.Scope.Clear();
                github.Events.OnTicketReceived = context
                    => SignInEvents.OnTicketReceived(context, SignInProviders.GitHub, p => p.FindFirstValue(ClaimTypes.NameIdentifier));
            });
    }

    /// <summary>What every OAuth provider shares: its registration, PKCE, no tokens kept, failures back to the page.</summary>
    private static void Remote(OAuthOptions remote, string provider, OAuthClientOptions client)
    {
        remote.ClientId = client.ClientId;
        remote.ClientSecret = client.ClientSecret;
        remote.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        remote.UsePkce = true;
        remote.SaveTokens = false;
        remote.Events.OnRemoteFailure = context => SignInEvents.OnRemoteFailure(context, provider);
    }
}
