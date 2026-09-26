using System.Globalization;
using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

/// <summary>
/// Where a feed page ends: the sort key of its last joke. The next page starts strictly after it, so
/// new jokes (and, for the top sort, votes on jokes elsewhere in the list) can't shift pages the way
/// an offset would. <see cref="Score"/> is only used by <see cref="JokeSort.TopVoted"/>.
/// </summary>
public sealed record JokeCursor(int Score, DateTime GeneratedAt, int Id)
{
    public static JokeCursor After(Joke joke) => new(joke.Up - joke.Down, joke.GeneratedAt, joke.Id);

    /// <summary>An opaque token for the API: "score.ticks.id".</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Score}.{GeneratedAt.Ticks}.{Id}");

    public static bool TryParse(string? value, out JokeCursor? cursor)
    {
        cursor = null;
        var parts = value?.Split('.');
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var score)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return false;

        cursor = new JokeCursor(score, new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
