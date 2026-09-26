using System.Collections.Concurrent;

namespace LazyDad.Api.Services;

/// <summary>
/// In-memory record of this process's most recent scheduler tick per language, served on
/// <c>/status</c>. Because it lives in the process, it describes only the revision that answers
/// the request, so the deployment smoke tests can check that the new revision generated jokes itself
/// (DB rows can't say that: a draining revision might have written them).
/// </summary>
public class SchedulerStatus
{
    private readonly ConcurrentDictionary<string, TickStatus> lastTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> nextTicks = new(StringComparer.OrdinalIgnoreCase);

    public void Record(TickStatus tick) => lastTicks[tick.Language] = tick;

    public IReadOnlyList<TickStatus> LastTicks => lastTicks.Values.OrderBy(t => t.Language, StringComparer.Ordinal).ToList();

    /// <summary>When the language's next scheduled tick is due (UTC).</summary>
    public void RecordNextTick(string language, DateTime dueAt) => nextTicks[language] = dueAt;

    /// <summary>The earliest scheduled tick of any language (the page's countdown), or <c>null</c> before the first is scheduled.</summary>
    public DateTime? NextTickAt => nextTicks.IsEmpty ? null : nextTicks.Values.Min();
}

/// <param name="Leaderboard">
/// <c>updated</c>, <c>unchanged</c> (also when the leaderboard is disabled or had nothing to judge),
/// or <c>failed</c>.
/// </param>
/// <param name="Error">The exception type only: /status is public, so no messages or details.</param>
public sealed record TickStatus(
    string Language,
    DateTime CompletedAt,
    bool Succeeded,
    IReadOnlyList<GeneratedJoke> Jokes,
    string Leaderboard,
    string? Error);

public sealed record GeneratedJoke(int Id, string Model);
