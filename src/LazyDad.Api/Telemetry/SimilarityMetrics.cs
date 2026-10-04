using System.Diagnostics.Metrics;

namespace LazyDad.Api.Telemetry;

/// <summary>
/// "You might also like": the Jev requests (outcome, cost, credits left), the profiles saved per method, and which
/// method ranked each request's suggestions (so a chart shows whether the embedding fallback was ever used). Every series
/// starts at 0 when the app starts (see <see cref="SchedulerMetrics.Initialize"/> for why), so resolve this at startup.
/// </summary>
public sealed class SimilarityMetrics
{
    public static readonly string[] JevOutcomes = ["succeeded", "failed", "out_of_credits"];
    public static readonly string[] ProfileKinds = ["jev", "embedding"];
    public static readonly string[] ProfileOutcomes = ["saved", "failed"];
    public static readonly string[] Methods = ["jev", "embedding", "words"];

    private readonly Counter<long> jevRequests;
    private readonly Counter<double> jevCost;
    private readonly Counter<long> profiles;
    private readonly Counter<long> similarRequests;
    /// <summary>
    /// How long a reported balance stays current. Only the replica that profiles (it holds the lease) hears a new
    /// balance, once a tick (every 4 hours), so an older reading would otherwise linger on the other replica, e.g. a
    /// low one after a top-up. The alert and dashboard take the highest current reading.
    /// </summary>
    public static readonly TimeSpan CreditsFreshFor = TimeSpan.FromHours(6);

    private readonly TimeProvider time;
    // The balance Jev reported last, and when; no measurement until a request has reported one, or once it's stale.
    private (double Usd, DateTimeOffset At)? jevCredits;

    public SimilarityMetrics(IMeterFactory meterFactory)
        : this(meterFactory, TimeProvider.System)
    {
    }

    /// <summary>For tests: a fake clock.</summary>
    internal SimilarityMetrics(IMeterFactory meterFactory, TimeProvider time)
    {
        this.time = time;
        var meter = meterFactory.Create(LazyDadTelemetry.Name);
        jevRequests = meter.CreateCounter<long>("lazydad.jev.requests", "{request}",
            "Jev requests by outcome: succeeded, failed, or out_of_credits (402).");
        // "{USD}", not "USD": an annotation keeps the unit out of the Prometheus names (lazydad_jev_cost_total, lazydad_jev_credits).
        jevCost = meter.CreateCounter<double>("lazydad.jev.cost", "{USD}", "What Jev charged, in US dollars, as it reports per request.");
        meter.CreateObservableGauge<double>("lazydad.jev.credits",
            () => jevCredits is { } credits && time.GetUtcNow() - credits.At < CreditsFreshFor
                ? [new Measurement<double>(credits.Usd)]
                : Array.Empty<Measurement<double>>(),
            "{USD}", "Jev credits left, in US dollars, as the last request reported.");
        profiles = meter.CreateCounter<long>("lazydad.joke.profiles", "{profile}",
            "Joke profiles for similar jokes, by kind (jev, embedding) and outcome (saved, failed).");
        similarRequests = meter.CreateCounter<long>("lazydad.similar.requests", "{request}",
            "\"You might also like\" requests by the method that ranked them: jev, embedding (the fallback) or words (neither).");

        foreach (var outcome in JevOutcomes)
            jevRequests.Add(0, new KeyValuePair<string, object?>("outcome", outcome));
        jevCost.Add(0);
        foreach (var kind in ProfileKinds)
            foreach (var outcome in ProfileOutcomes)
                profiles.Add(0, new("kind", kind), new("outcome", outcome));
        foreach (var method in Methods)
            similarRequests.Add(0, new KeyValuePair<string, object?>("method", method));
    }

    public void RecordJevRequest(string outcome) => jevRequests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordJevCost(double usd) => jevCost.Add(usd);

    public void RecordJevCredits(double usd) => jevCredits = (usd, time.GetUtcNow());

    public void RecordProfile(string kind, string outcome) => profiles.Add(1, new("kind", kind), new("outcome", outcome));

    public void RecordSimilar(string method) => similarRequests.Add(1, new KeyValuePair<string, object?>("method", method));
}
