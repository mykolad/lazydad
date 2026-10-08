using System.Text.Json;
using LazyDad.Api.Configuration;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Checks an OAuth provider's registration without a reader: exchanges a made-up code at its token endpoint with the
/// app's client id and secret. The code is never valid, so the answer only says whether the provider accepts the client:
/// "bad code" means it does (<see cref="ProviderState.Valid"/>), "bad client" that it doesn't (an id or a secret that's
/// wrong, expired or replaced: <see cref="ProviderState.Invalid"/>). No answer, a timeout, 429 or 5xx is
/// <see cref="ProviderState.Unreachable"/>. Any other answer is <see cref="ProviderState.Invalid"/> too, and logged.
/// </summary>
public sealed class OAuthCodeProbe : ISignInProbe
{
    public const string MadeUpCode = "lazydad-probe";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string tokenEndpoint;
    private readonly OAuthClientOptions client;
    private readonly IReadOnlySet<string> validErrors;
    private readonly IReadOnlySet<string> invalidErrors;
    private readonly IHttpClientFactory httpClients;
    private readonly ILogger<OAuthCodeProbe> logger;

    public OAuthCodeProbe(string provider, string tokenEndpoint, OAuthClientOptions client, IReadOnlySet<string> validErrors,
        IReadOnlySet<string> invalidErrors, IHttpClientFactory httpClients, ILogger<OAuthCodeProbe> logger)
    {
        Provider = provider;
        this.tokenEndpoint = tokenEndpoint;
        this.client = client;
        this.validErrors = validErrors;
        this.invalidErrors = invalidErrors;
        this.httpClients = httpClients;
        this.logger = logger;
    }

    public string Provider { get; }

    /// <summary>
    /// GitHub answers 200 with the error in the body: <c>bad_verification_code</c> for a code it doesn't know,
    /// <c>incorrect_client_credentials</c> for a wrong id or secret
    /// (https://docs.github.com/en/apps/oauth-apps/maintaining-oauth-apps/troubleshooting-oauth-app-access-token-request-errors).
    /// The standard OAuth errors (RFC 6749) count too.
    /// </summary>
    public static OAuthCodeProbe GitHub(string tokenEndpoint, OAuthClientOptions client, IServiceProvider services)
        => new(SignInProviders.GitHub, tokenEndpoint, client,
            new HashSet<string> { "bad_verification_code", "invalid_grant" },
            new HashSet<string> { "incorrect_client_credentials", "invalid_client", "unauthorized_client", "redirect_uri_mismatch" },
            services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<ILogger<OAuthCodeProbe>>());

    /// <summary>
    /// A provider that answers as RFC 6749 says: <c>invalid_grant</c> for a code it doesn't know, <c>invalid_client</c> (or
    /// <c>unauthorized_client</c>) for a wrong id or secret. Google checks the client first, so a made-up code with the
    /// right client is always <c>invalid_grant</c>.
    /// </summary>
    public static OAuthCodeProbe Standard(string provider, string tokenEndpoint, OAuthClientOptions client, IServiceProvider services)
        => new(provider, tokenEndpoint, client,
            new HashSet<string> { "invalid_grant" },
            new HashSet<string> { "invalid_client", "unauthorized_client" },
            services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<ILogger<OAuthCodeProbe>>());

    public async Task<ProviderState> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["code"] = MadeUpCode,
                ["grant_type"] = "authorization_code",
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Options.Set(SignInBackchannel.IsProbe, true);
        try
        {
            using var response = await httpClients.CreateClient(SignInBackchannel.ClientName(Provider)).SendAsync(request, timeout.Token);
            if (SignInBackchannel.Outcome(response) == SignInBackchannel.Unreachable)
            {
                logger.LogWarning("The {Provider} sign-in probe got {Status} from the token endpoint.", Provider, (int)response.StatusCode);
                return ProviderState.Unreachable;
            }

            var error = ErrorCode(await response.Content.ReadAsStringAsync(timeout.Token));
            if (error is not null && validErrors.Contains(error))
                return ProviderState.Valid;
            if (error is not null && invalidErrors.Contains(error))
                logger.LogError("{Provider} refused the app's client id or secret ({Error}): sign-in with it fails.", Provider, error);
            else
                logger.LogError("The {Provider} sign-in probe got an answer it doesn't know: {Status}, error {Error}.",
                    Provider, (int)response.StatusCode, error ?? "(none)");
            return ProviderState.Invalid;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("The {Provider} sign-in probe got no answer: {Error}", Provider, ex.Message);
            return ProviderState.Unreachable;
        }
    }

    // The "error" of an OAuth error answer (it never carries a secret); null if the body isn't one.
    internal static string? ErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
