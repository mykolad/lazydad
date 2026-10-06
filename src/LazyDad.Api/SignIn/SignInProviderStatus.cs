using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Whether a provider's registration works, as its last probe found (<see cref="SignInProbeService"/>): <c>off</c>
/// (not configured here), <c>pending</c> (not probed yet), <c>valid</c> (the provider accepts the client id and secret),
/// <c>invalid</c> (it refuses them: a wrong id, or a secret that's expired or was replaced), <c>unreachable</c> (no
/// answer, a timeout or a server error).
/// </summary>
[JsonConverter(typeof(ProviderStateJsonConverter))]
public enum ProviderState
{
    Off,
    Pending,
    Valid,
    Invalid,
    Unreachable,
}

/// <summary>On <c>/status</c> by name, in camelCase: <c>"valid"</c>.</summary>
public sealed class ProviderStateJsonConverter() : JsonStringEnumConverter<ProviderState>(JsonNamingPolicy.CamelCase);

/// <summary>Each provider's state, for <c>/status</c> and the metrics. Every known provider is listed, <c>off</c> if not probed.</summary>
public sealed class SignInProviderStatus
{
    private readonly ConcurrentDictionary<string, ProviderState> states = new();

    public SignInProviderStatus(IEnumerable<ISignInProbe> probes)
    {
        foreach (var provider in SignInProviders.All)
            states[provider] = ProviderState.Off;
        foreach (var probe in probes)
            states[probe.Provider] = ProviderState.Pending;
    }

    public IReadOnlyDictionary<string, ProviderState> All
        => SignInProviders.All.ToDictionary(provider => provider, provider => states[provider]);

    public ProviderState this[string provider] => states[provider];

    public void Set(string provider, ProviderState state) => states[provider] = state;
}
