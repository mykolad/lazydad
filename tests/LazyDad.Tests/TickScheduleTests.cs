using LazyDad.Api.Services;

namespace LazyDad.Tests;

public class TickScheduleTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    private static DateTime Utc(string time) => DateTime.SpecifyKind(DateTime.Parse($"2026-09-28T{time}"), DateTimeKind.Utc);

    [Theory]
    [InlineData("10:37:00", "12:00:00")]
    [InlineData("00:00:00", "04:00:00")]   // strictly after: a due time itself moves to the next
    [InlineData("11:59:59", "12:00:00")]
    [InlineData("12:00:00", "16:00:00")]
    [InlineData("20:00:01", "00:00:00")]   // the next day
    public void NextDue_IsTheNextWholePeriodSinceMidnightUtc(string after, string expected)
    {
        var due = TickSchedule.NextDue(Utc(after), FourHours);

        var expectedDue = Utc(expected);
        if (expectedDue <= Utc(after))
            expectedDue = expectedDue.AddDays(1);
        Assert.Equal(expectedDue, due);
        Assert.Equal(DateTimeKind.Utc, due.Kind);
    }

    [Fact]
    public void NextDue_WithAnHourlyPeriod_IsOnTheHour()
        => Assert.Equal(Utc("11:00:00"), TickSchedule.NextDue(Utc("10:00:30"), TimeSpan.FromHours(1)));

    [Theory]
    [InlineData("10:37:00", "16:00:00")]   // 12:00 would be 1h23m after the startup batch: skipped
    [InlineData("09:59:00", "12:00:00")]   // 2h01m later: kept
    [InlineData("10:00:00", "16:00:00")]   // exactly half a period before 12:00: skipped too
    [InlineData("11:58:00", "16:00:00")]   // a restart just before a due time doesn't add a batch 2 minutes later
    public void FirstDueAfterStartup_IsAtLeastHalfAPeriodAway(string startedAt, string expected)
    {
        var due = TickSchedule.FirstDueAfterStartup(Utc(startedAt), FourHours);

        Assert.Equal(Utc(expected), due);
        Assert.InRange(due - Utc(startedAt), FourHours / 2, FourHours * 1.5);
    }
}
