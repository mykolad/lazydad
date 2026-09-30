using LazyDad.Api.Services;

namespace LazyDad.Tests;

public class SchedulerStatusTests
{
    private static TickStatus Tick(string language, bool succeeded)
        => new(language, DateTime.UtcNow, succeeded, [], "unchanged", null);

    [Fact]
    public void Record_KeepsOnlyTheLatestTickPerLanguage_CaseInsensitively()
    {
        var status = new SchedulerStatus();

        status.Record(Tick("Ukrainian", succeeded: false));
        status.Record(Tick("ukrainian", succeeded: true));

        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
    }

    [Fact]
    public void LastTicks_AreOrderedByLanguage()
    {
        var status = new SchedulerStatus();

        status.Record(Tick("Ukrainian", succeeded: true));
        status.Record(Tick("English", succeeded: true));

        Assert.Equal(["English", "Ukrainian"], status.LastTicks.Select(t => t.Language));
    }

    [Fact]
    public void NextTickAt_IsNullUntilATickIsScheduled_ThenTheEarliestLanguage()
    {
        var status = new SchedulerStatus();
        Assert.Null(status.NextTickAt);

        var early = new DateTime(2026, 9, 26, 4, 0, 0, DateTimeKind.Utc);
        status.RecordNextTick("Ukrainian", early.AddHours(2));
        status.RecordNextTick("English", early);
        status.RecordNextTick("ukrainian", early.AddHours(1));

        Assert.Equal(early, status.NextTickAt);
    }

    [Fact]
    public void SavedJokes_AreNewestFirst_AndOnlyTheLatestAreKept()
    {
        var status = new SchedulerStatus();
        var total = SchedulerStatus.SavedJokesKept + 5;

        for (var id = 1; id <= total; id++)
            status.RecordSavedJoke(new SavedJoke("Ukrainian", id, "fast", DateTime.UtcNow));

        Assert.Equal(Enumerable.Range(6, SchedulerStatus.SavedJokesKept).Reverse(), status.SavedJokes.Select(j => j.Id));
    }

    [Fact]
    public void SavedJokes_KeepExactlyTheLatest_WhenSavedInParallel()
    {
        var status = new SchedulerStatus();
        var reads = new List<int>();

        // Models save in parallel; readers (/status) can ask at any moment.
        Parallel.For(1, 2001, id =>
        {
            status.RecordSavedJoke(new SavedJoke("Ukrainian", id, "fast", DateTime.UtcNow));
            var count = status.SavedJokes.Count;
            lock (reads) reads.Add(count);
        });

        Assert.Equal(SchedulerStatus.SavedJokesKept, status.SavedJokes.Count);
        Assert.All(reads, count => Assert.InRange(count, 1, SchedulerStatus.SavedJokesKept));
    }
}
