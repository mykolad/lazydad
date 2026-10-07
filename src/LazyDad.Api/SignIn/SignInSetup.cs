using AspNet.Security.OAuth.GitHub;
using Azure.Identity;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using LazyDad.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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

    /// <summary>
    /// How long a sign-in lasts, renewed while it's used (once more than half has passed). The cookie itself outlives the
    /// browser session only when the reader ticked "Keep me signed in" (<c>persist</c> on the sign-in URL): EU guidance
    /// (WP29 Opinion 04/2012) exempts sign-in cookies from consent, but one kept across sessions only by the user's choice.
    /// Otherwise it's a session cookie, and the browser drops it on closing.
    /// </summary>
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
        services.AddSingleton<SignInProviderStatus>();
        services.AddHostedService<SignInProbeService>();

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
        // while sign-in is on and the provider is configured. The rest come with their issues (#78, #79);
        // Development's signs in at once, only in Development.
        var options = builder.Configuration.GetSection(SignInOptions.SectionName).Get<SignInOptions>() ?? new SignInOptions();
        if (!options.Enabled)
            return;
        if (builder.Environment.IsDevelopment())
            authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentSignInHandler>(SignInProviders.Development, null);
        if (options.GitHub.Configured)
        {
            authentication.AddGitHub(SignInProviders.GitHub, github =>
            {
                Remote(github, SignInProviders.GitHub, options.GitHub);
                // No scopes: the public profile is enough, and its numeric id is the account id (never the login, which
                // the reader can change). GitHub ids are public, which is why the voter key is keyed with the pepper.
                github.Scope.Clear();
                github.Events.OnTicketReceived = context
                    => SignInEvents.OnTicketReceived(context, SignInProviders.GitHub, p => p.FindFirstValue(ClaimTypes.NameIdentifier));
            });
            Backchannel<GitHubAuthenticationOptions>(services, SignInProviders.GitHub, uri =>
                uri.AbsoluteUri == GitHubAuthenticationDefaults.TokenEndpoint ? SignInBackchannel.Token
                : uri.AbsoluteUri == GitHubAuthenticationDefaults.UserInformationEndpoint ? SignInBackchannel.UserInfo
                : SignInBackchannel.Other);
            services.AddSingleton<ISignInProbe>(provider => OAuthCodeProbe.GitHub(options.GitHub, provider));
        }
        if (options.Google.Configured)
        {
            // Google's id tokens name their issuer with or without the scheme (its OpenID Connect documentation).
            OpenIdConnect(authentication, services, SignInProviders.Google, OpenIdProviders.Google, options.Google,
                google => google.TokenValidationParameters.ValidIssuers = [OpenIdProviders.Google.Authority, "accounts.google.com"]);
            services.AddSingleton<ISignInProbe>(provider => OAuthCodeProbe.Standard(SignInProviders.Google, OpenIdProviders.Google.TokenEndpoint,
                options.Google, provider));
        }
        if (options.Microsoft.Configured)
            AddMicrosoft(authentication, services, options.Microsoft);
    }

    /// <summary>
    /// Personal Microsoft accounts and work or school ones, through "common". Its <c>sub</c> is unique to the app
    /// registration, so replacing the registration makes every Microsoft voter new. The registration proves itself with a
    /// secret, or with a token of a managed identity it trusts (no secret at all).
    /// </summary>
    private static void AddMicrosoft(AuthenticationBuilder authentication, IServiceCollection services, MicrosoftClientOptions microsoft)
    {
        if (microsoft.UsesManagedIdentity)
            services.AddSingleton<IClientAssertion>(new ManagedIdentityAssertion(microsoft.ManagedIdentityClientId));
        OpenIdConnect(authentication, services, SignInProviders.Microsoft, OpenIdProviders.Microsoft, microsoft, oidc =>
        {
            oidc.TokenValidationParameters.IssuerValidator = OpenIdProviders.MicrosoftIssuer;
            if (microsoft.UsesManagedIdentity)
                oidc.Events.OnAuthorizationCodeReceived = async context =>
                {
                    var request = context.TokenEndpointRequest!;
                    request.ClientSecret = null;
                    request.ClientAssertionType = IClientAssertion.Type;
                    request.ClientAssertion = await context.HttpContext.RequestServices.GetRequiredService<IClientAssertion>()
                        .GetAsync(context.HttpContext.RequestAborted);
                };
        });
        services.AddSingleton<ISignInProbe>(provider => new ClientCredentialsProbe(SignInProviders.Microsoft,
            OpenIdProviders.MicrosoftTenantTokenEndpoint(microsoft.TenantId), microsoft.ClientId, microsoft.ClientSecret,
            microsoft.UsesManagedIdentity ? provider.GetRequiredService<IClientAssertion>() : null,
            // Any resource will do; the app needs no permission on it to get a token for itself.
            "https://graph.microsoft.com/.default",
            provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<ILogger<ClientCredentialsProbe>>()));
    }

    /// <summary>
    /// An OpenID Connect provider: the code flow with PKCE, the <c>openid</c> scope only (no email, no profile), and the
    /// account id from the id token's <c>sub</c>, which the handler validates (signature from the provider's published
    /// keys, issuer, audience, nonce). No user info call, nothing kept but the voter key. The answer comes back in the
    /// query (a top-level GET, as with GitHub) rather than as a cross-site form post.
    /// </summary>
    private static void OpenIdConnect(AuthenticationBuilder authentication, IServiceCollection services, string provider,
        OpenIdProvider endpoints, OAuthClientOptions client, Action<OpenIdConnectOptions> configure)
    {
        authentication.AddOpenIdConnect(provider, oidc =>
        {
            oidc.Authority = endpoints.Authority;
            oidc.ClientId = client.ClientId;
            oidc.ClientSecret = client.ClientSecret;
            oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            oidc.ResponseType = OpenIdConnectResponseType.Code;
            oidc.ResponseMode = OpenIdConnectResponseMode.Query;
            oidc.UsePkce = true;
            oidc.Scope.Clear();
            oidc.Scope.Add("openid");
            oidc.GetClaimsFromUserInfoEndpoint = false;
            // The token's own claim names ("sub", not a long URI), and nothing copied into the user but what's mapped.
            oidc.MapInboundClaims = false;
            oidc.SaveTokens = false;
            oidc.CallbackPath = $"/signin-{provider}";
            // Never used (signing out is the site's own), but each scheme needs its own paths.
            oidc.SignedOutCallbackPath = $"/signout-callback-{provider}";
            oidc.RemoteSignOutPath = $"/signout-{provider}";
            oidc.Events.OnTicketReceived = context => SignInEvents.OnTicketReceived(context, provider, p => p.FindFirstValue("sub"));
            oidc.Events.OnRemoteFailure = context => SignInEvents.OnRemoteFailure(context, provider);
            configure(oidc);
        });
        // The discovery document and the signing keys go through it too ("other"), about once a day.
        Backchannel<OpenIdConnectOptions>(services, provider, uri =>
            uri.AbsoluteUri == endpoints.TokenEndpoint ? SignInBackchannel.Token : SignInBackchannel.Other);
    }

    /// <summary>
    /// The provider's back channel: a named HTTP client (<see cref="SignInBackchannel.ClientName"/>) whose calls are
    /// timed, shared by its handler and its probe. Tests replace its primary handler with a fake provider.
    /// </summary>
    private static void Backchannel<TOptions>(IServiceCollection services, string provider, Func<Uri, string> operation)
        where TOptions : RemoteAuthenticationOptions
    {
        services.AddHttpClient(SignInBackchannel.ClientName(provider), client =>
            {
                // What the handler's own back channel would have: GitHub's API refuses a request without a user agent.
                client.DefaultRequestHeaders.UserAgent.ParseAdd("LazyDad (+https://lazydad.fyi)");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = SignInBackchannel.MaxResponseBytes;
            })
            // The handler keeps its client for the app's lifetime, so connections are renewed instead (DNS changes).
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
            .AddHttpMessageHandler(services => new SignInBackchannel.Timing(provider, operation, services.GetRequiredService<SignInMetrics>()));
        services.AddOptions<TOptions>(provider)
            .Configure<IHttpClientFactory>((remote, clients) => remote.Backchannel = clients.CreateClient(SignInBackchannel.ClientName(provider)));
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
