namespace LazyDad.Api.Services;

/// <summary>
/// When a language's regular ticks are due: fixed UTC times, every whole period counted from midnight UTC. With a
/// 4-hour period that's 00:00, 04:00, 08:00 ... UTC, the same for every replica and unchanged by restarts: a restart
/// adds its startup batch, but the rhythm stays. (Counted from <see cref="DateTime.MinValue"/>, which is a midnight,
/// so periods that divide 24 hours land on the same times every day.)
/// </summary>
public static class TickSchedule
{
    /// <summary>The first due time strictly after <paramref name="after"/> (UTC).</summary>
    public static DateTime NextDue(DateTime after, TimeSpan period)
        => new((after.Ticks / period.Ticks + 1) * period.Ticks, DateTimeKind.Utc);

    /// <summary>
    /// The first regular tick after a startup tick at <paramref name="startedAt"/>: the first due time at least half a
    /// period later, so a restart just before a due time doesn't produce two batches minutes apart. The startup
    /// batch stands in for the due time it skips; the gap to the next batch is then between half a period and one
    /// and a half.
    /// </summary>
    public static DateTime FirstDueAfterStartup(DateTime startedAt, TimeSpan period)
        => NextDue(startedAt + period / 2, period);
}
