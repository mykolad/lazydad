using Azure.Identity;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using LazyDad.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

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

        var options = builder.Configuration.GetSection(SignInOptions.SectionName).Get<SignInOptions>() ?? new SignInOptions();
        var providers = new List<string>();
        if (options.Enabled && builder.Environment.IsDevelopment())
            providers.Add(SignInProviders.Development);
        services.AddSingleton(new EnabledSignInProviders(providers));
        services.AddSingleton<SignInMetrics>();
        services.AddSingleton<KeyRingCheck>();

        // The key ring lives in the database, on its read-write connection (never a read-only replica: a key is used as
        // soon as it's made), and is read about once a day. Its keys are encrypted with the Key Vault key; the validator
        // makes that key required wherever sign-in is on, except in Development.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToDbContext<LazyDadDbContext>();
        var keyRing = builder.Configuration.GetSection(KeyRingOptions.SectionName).Get<KeyRingOptions>() ?? new KeyRingOptions();
        if (keyRing.TryGetKeyId(out var keyId))
            dataProtection.ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential());

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
            });
    }
}
