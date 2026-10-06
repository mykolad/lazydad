using System.Diagnostics;
using LazyDad.Api.Telemetry;

namespace LazyDad.Api.SignIn;

/// <summary>
/// The HTTP client a provider's handler talks to it with (its back channel: the token exchange and the user info), one
/// named client per provider. <see cref="Timing"/> times each call into
/// <c>lazydad_signin_provider_duration_seconds</c>: the load test can't measure real providers, so production does.
/// </summary>
public static class SignInBackchannel
{
    public const string Token = "token";
    public const string UserInfo = "userinfo";
    public const string Other = "other";
    public const string Probe = "probe";

    /// <summary>A call answered with a success status.</summary>
    public const string Ok = "ok";
    /// <summary>A call answered with a client error (4xx).</summary>
    public const string Error = "error";
    /// <summary>No answer: a timeout, a network error, or a server error (429 or 5xx).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>A request the probe sends: it times itself, with its own outcome.</summary>
    public static readonly HttpRequestOptionsKey<bool> IsProbe = new("lazydad.signin.probe");

    public static string ClientName(string provider) => $"signin-{provider}";

    public static string Outcome(HttpResponseMessage response)
        => (int)response.StatusCode is 429 or >= 500 ? Unreachable : response.IsSuccessStatusCode ? Ok : Error;

    /// <summary>
    /// Times each call to the provider. <paramref name="operation"/> names it from its address (the token or user info
    /// endpoint), so the metric's tags stay a few known values.
    /// </summary>
    public sealed class Timing(string provider, Func<Uri, string> operation, SignInMetrics metrics) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Options.TryGetValue(IsProbe, out var isProbe) && isProbe)
                return await base.SendAsync(request, cancellationToken);

            var name = operation(request.RequestUri!);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                metrics.RecordProviderCall(provider, name, Outcome(response), Stopwatch.GetElapsedTime(started));
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                metrics.RecordProviderCall(provider, name, Unreachable, Stopwatch.GetElapsedTime(started));
                throw;
            }
        }
    }
}
