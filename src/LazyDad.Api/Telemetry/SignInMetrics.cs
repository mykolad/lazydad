using System.Diagnostics.Metrics;
using LazyDad.Api.SignIn;

namespace LazyDad.Api.Telemetry;

/// <summary>
/// Sign-ins per provider and outcome: started (sent to the provider), completed (signed in) or failed (the provider or
/// the callback failed). Completed over started is how many readers finish a sign-in. Every enabled provider's series
/// start at 0 when the app starts (see <see cref="SchedulerMetrics.Initialize"/> for why), so resolve this at startup.
/// </summary>
public sealed class SignInMetrics
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public static readonly string[] Outcomes = [Started, Completed, Failed];

    private readonly Counter<long> signIns;

    public SignInMetrics(IMeterFactory meterFactory, EnabledSignInProviders providers)
    {
        var meter = meterFactory.Create(LazyDadTelemetry.Name);
        signIns = meter.CreateCounter<long>("lazydad.signins", "{sign-in}",
            "Sign-ins by provider and outcome: started, completed or failed.");
        foreach (var provider in providers.Names)
            foreach (var outcome in Outcomes)
                signIns.Add(0, new("provider", provider), new("outcome", outcome));
    }

    public void Record(string provider, string outcome) => signIns.Add(1, new("provider", provider), new("outcome", outcome));
}
