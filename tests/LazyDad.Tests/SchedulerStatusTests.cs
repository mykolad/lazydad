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
}
