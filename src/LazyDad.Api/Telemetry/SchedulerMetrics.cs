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

    public void RecordTick(string language, string outcome)
        => ticks.Add(1, new("language", language), new("outcome", outcome));

    public void RecordJoke(string language, string model, string outcome)
        => jokes.Add(1, new("language", language), new("model", model), new("outcome", outcome));

    public void RecordLeaderboard(string language, string outcome)
        => leaderboardUpdates.Add(1, new("language", language), new("outcome", outcome));
}
