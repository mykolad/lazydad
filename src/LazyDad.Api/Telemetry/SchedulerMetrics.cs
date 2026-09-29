using System.Diagnostics.Metrics;

namespace LazyDad.Api.Telemetry;

/// <summary>
/// Counters for what the scheduler did, for charts and alerts (e.g. "a tick failed", "no joke saved for hours").
/// Tags stay few and bounded (language, model, outcome), so the metric series count stays small.
/// </summary>
public sealed class SchedulerMetrics
{
    private readonly Counter<long> ticks;
    private readonly Counter<long> jokes;
    private readonly Counter<long> leaderboardUpdates;

    public SchedulerMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(LazyDadTelemetry.Name);
        ticks = meter.CreateCounter<long>("lazydad.scheduler.ticks", "{tick}",
            "Scheduler ticks by outcome: succeeded, failed, or skipped (another replica generated this period).");
        jokes = meter.CreateCounter<long>("lazydad.jokes", "{joke}",
            "Jokes per model by outcome: saved, empty (the model returned no text), or failed (generating or saving it).");
        leaderboardUpdates = meter.CreateCounter<long>("lazydad.leaderboard.updates", "{update}",
            "Leaderboard updates after each tick that ran, by outcome: updated, unchanged, or failed.");
    }

    /// <summary>
    /// Starts every series a language can report at 0. A counter series only appears with its first measurement,
    /// and Prometheus' <c>increase()</c> and <c>rate()</c> count only what happens after a series' first sample. So
    /// without this, the first tick after each replica start (deploy, restart, scale-out) would never show in charts
    /// or alerts, not even a failed one.
    /// </summary>
    public void Initialize(string language, IEnumerable<string> models)
    {
        foreach (var outcome in (string[])["succeeded", "failed", "skipped"])
            ticks.Add(0, new("language", language), new("outcome", outcome));
        foreach (var outcome in (string[])["updated", "unchanged", "failed"])
            leaderboardUpdates.Add(0, new("language", language), new("outcome", outcome));
        foreach (var model in models)
            foreach (var outcome in (string[])["saved", "empty", "failed"])
                jokes.Add(0, new("language", language), new("model", model), new("outcome", outcome));
    }

    public void RecordTick(string language, string outcome)
        => ticks.Add(1, new("language", language), new("outcome", outcome));

    public void RecordJoke(string language, string model, string outcome)
        => jokes.Add(1, new("language", language), new("model", model), new("outcome", outcome));

    public void RecordLeaderboard(string language, string outcome)
        => leaderboardUpdates.Add(1, new("language", language), new("outcome", outcome));
}
