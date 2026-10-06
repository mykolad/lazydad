using System.Diagnostics.Metrics;
using LazyDad.Api.SignIn;

namespace LazyDad.Api.Telemetry;

/// <summary>
/// Sign-ins per provider and outcome: started (sent to the provider), completed (signed in) or failed (the provider or
/// the callback failed). Completed over started is how many readers finish a sign-in. Every enabled provider's series
/// start at 0 when the app starts (see <see cref="SchedulerMetrics.Initialize"/> for why), so resolve this at startup.
/// Also how fast the providers answer the app's own calls (<see cref="SignInBackchannel"/> and the probe), and each
/// provider's state from its last probe (<see cref="SignInProviderStatus"/>).
/// </summary>
public sealed class SignInMetrics
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public static readonly string[] Outcomes = [Started, Completed, Failed];

    // Seconds: a provider usually answers in a tenth of a second or so; the probe gives up after 10.
    private static readonly IReadOnlyList<double> DurationBuckets = [0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private readonly Counter<long> signIns;
    private readonly Histogram<double> providerCalls;

    public SignInMetrics(IMeterFactory meterFactory, EnabledSignInProviders providers, SignInProviderStatus status)
    {
        var meter = meterFactory.Create(LazyDadTelemetry.Name);
        signIns = meter.CreateCounter<long>("lazydad.signins", "{sign-in}",
            "Sign-ins by provider and outcome: started, completed or failed.");
        providerCalls = meter.CreateHistogram("lazydad.signin.provider.duration", "s",
            "The app's calls to a sign-in provider, by provider, operation (token, userinfo, other, probe) and outcome " +
            "(ok, error, unreachable; a probe's: valid, invalid, unreachable).",
            tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
        // 1 for each probed provider's current state; nothing before its first probe or for a provider that's off.
        meter.CreateObservableGauge("lazydad.signin.provider.state",
            () => status.All
                .Where(p => p.Value is not (ProviderState.Off or ProviderState.Pending))
                .Select(p => new Measurement<int>(1, new("provider", p.Key), new("state", p.Value.ToString().ToLowerInvariant()))),
            "{provider}", "Each sign-in provider's state from its last probe: valid, invalid or unreachable.");
        foreach (var provider in providers.Names)
            foreach (var outcome in Outcomes)
                signIns.Add(0, new("provider", provider), new("outcome", outcome));
    }

    public void Record(string provider, string outcome) => signIns.Add(1, new("provider", provider), new("outcome", outcome));

    public void RecordProviderCall(string provider, string operation, string outcome, TimeSpan duration)
        => providerCalls.Record(duration.TotalSeconds, new("provider", provider), new("operation", operation), new("outcome", outcome));
}
