using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;

namespace LazyDad.Tests;

public class JokeCursorTests
{
    [Fact]
    public void After_TakesTheJokesNetScore_Time_AndId()
    {
        var generatedAt = new DateTime(2026, 9, 23, 4, 0, 0, DateTimeKind.Utc);

        var cursor = JokeCursor.After(new Joke { Id = 9, GeneratedAt = generatedAt, Up = 2, Down = 5 });

        Assert.Equal(new JokeCursor(-3, generatedAt, 9), cursor);
    }

    [Fact]
    public void ToString_RoundTripsThroughTryParse()
    {
        var cursor = new JokeCursor(-3, new DateTime(2026, 9, 23, 4, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567), 9);

        Assert.True(JokeCursor.TryParse(cursor.ToString(), out var parsed));
        Assert.Equal(cursor, parsed);
        Assert.Equal(cursor.GeneratedAt.Ticks, parsed!.GeneratedAt.Ticks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("x.2.3")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.-3")]
    [InlineData("1.99999999999999999999.3")]
    public void TryParse_RejectsAnythingElse(string? value)
    {
        Assert.False(JokeCursor.TryParse(value, out var cursor));
        Assert.Null(cursor);
    }
}
