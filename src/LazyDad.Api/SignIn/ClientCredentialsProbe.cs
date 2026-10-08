namespace LazyDad.Api.SignIn;

/// <summary>
/// Checks a registration by asking for a token as the app itself (the client credentials grant) and dropping it: a token
/// means the provider accepts the app's secret or assertion (<see cref="ProviderState.Valid"/>), <c>invalid_client</c> or
/// <c>unauthorized_client</c> that it doesn't. For Microsoft, whose common endpoint reads a made-up code before the client
/// and so can't tell a wrong secret from a right one (<see cref="OAuthCodeProbe"/>'s way).
/// </summary>
public sealed class ClientCredentialsProbe : ISignInProbe
{
    private readonly string tokenEndpoint;
    private readonly string clientId;
    private readonly string clientSecret;
    private readonly IClientAssertion? assertion;
    private readonly string scope;
    private readonly IHttpClientFactory httpClients;
    private readonly ILogger<ClientCredentialsProbe> logger;

    /// <param name="assertion">Sent instead of <paramref name="clientSecret"/> when there's one.</param>
    public ClientCredentialsProbe(string provider, string tokenEndpoint, string clientId, string clientSecret, IClientAssertion? assertion,
        string scope, IHttpClientFactory httpClients, ILogger<ClientCredentialsProbe> logger)
    {
        Provider = provider;
        this.tokenEndpoint = tokenEndpoint;
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.assertion = assertion;
        this.scope = scope;
        this.httpClients = httpClients;
        this.logger = logger;
    }

    public string Provider { get; }

    public async Task<ProviderState> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OAuthCodeProbe.Timeout);
        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
            };
            if (assertion is null)
                form["client_secret"] = clientSecret;
            else
            {
                form["client_assertion_type"] = IClientAssertion.Type;
                form["client_assertion"] = await assertion.GetAsync(timeout.Token);
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Accept.ParseAdd("application/json");
            request.Options.Set(SignInBackchannel.IsProbe, true);

            using var response = await httpClients.CreateClient(SignInBackchannel.ClientName(Provider)).SendAsync(request, timeout.Token);
            if (SignInBackchannel.Outcome(response) == SignInBackchannel.Unreachable)
            {
                logger.LogWarning("The {Provider} sign-in probe got {Status} from the token endpoint.", Provider, (int)response.StatusCode);
                return ProviderState.Unreachable;
            }
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            // Only a token proves the credential works; a success without one is an answer the sign-in couldn't use either.
            if (response.IsSuccessStatusCode && HasAccessToken(body))
                return ProviderState.Valid;

            var error = OAuthCodeProbe.ErrorCode(body);
            // RFC 6749's errors for a provider having trouble, which Entra sends with a 400: its outage, not the credentials.
            if (error is "server_error" or "temporarily_unavailable")
            {
                logger.LogWarning("The {Provider} sign-in probe got {Error} from the token endpoint.", Provider, error);
                return ProviderState.Unreachable;
            }
            logger.LogError("{Provider} refused the app's credentials ({Status}, error {Error}): sign-in with it fails.",
                Provider, (int)response.StatusCode, error ?? "(none)");
            return ProviderState.Invalid;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("The {Provider} sign-in probe got no answer: {Error}", Provider, ex.Message);
            return ProviderState.Unreachable;
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            // The managed identity couldn't get its token: the app's own setup, so the sign-in would fail too.
            logger.LogError(ex, "The {Provider} sign-in probe couldn't get the managed identity's token for the client assertion.", Provider);
            return ProviderState.Invalid;
        }
    }

    private static bool HasAccessToken(string body)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(token.GetString());
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
