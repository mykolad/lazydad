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
    // The models' saves run in parallel: adding, trimming and reading share one lock, so the list never holds more or
    // fewer than the latest SavedJokesKept.
    private readonly Queue<SavedJoke> savedJokes = new();
    private readonly Lock savedJokesLock = new();

    public void Record(TickStatus tick) => lastTicks[tick.Language] = tick;

    public IReadOnlyList<TickStatus> LastTicks => lastTicks.Values.OrderBy(t => t.Language, StringComparer.Ordinal).ToList();

    /// <summary>
    /// When the language's next scheduled tick is due (UTC). Recorded after a tick completes, so while
    /// one runs the value is already due (in the past), and a change means a batch is complete.
    /// </summary>
    public void RecordNextTick(string language, DateTime dueAt) => nextTicks[language] = dueAt;

    /// <summary>The earliest scheduled tick of any language (the page's countdown), or <c>null</c> before the first is scheduled.</summary>
    public DateTime? NextTickAt => nextTicks.IsEmpty ? null : nextTicks.Values.Min();

    /// <summary>
    /// Records a joke as soon as this process saved it, during its tick, not at the tick's end. So a revision shows it
    /// works (a model answered, the database took the joke) without waiting for its slowest model.
    /// </summary>
    public void RecordSavedJoke(SavedJoke joke)
    {
        lock (savedJokesLock)
        {
            savedJokes.Enqueue(joke);
            while (savedJokes.Count > SavedJokesKept)
                savedJokes.Dequeue();
        }
    }

    /// <summary>The jokes this process saved most recently, newest first (at most <see cref="SavedJokesKept"/>).</summary>
    public IReadOnlyList<SavedJoke> SavedJokes
    {
        get
        {
            lock (savedJokesLock)
                return savedJokes.Reverse().ToList();
        }
    }

    public const int SavedJokesKept = 20;
}

/// <param name="Leaderboard">
/// <c>updated</c>, <c>unchanged</c> (also when the leaderboard is disabled or had nothing to judge),
/// <c>failed</c>, or <c>skipped</c> (another replica generated this period, or the tick came too late for its slot; no jokes, nothing
/// judged).
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

/// <param name="SavedAt">When this process saved it (UTC).</param>
public sealed record SavedJoke(string Language, int Id, string Model, DateTime SavedAt);
