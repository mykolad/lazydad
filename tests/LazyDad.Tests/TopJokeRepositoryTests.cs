using LazyDad.Data;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Tests;

/// <summary>
/// Runs against a real database (SQLite in memory, or SQL Server with the migrations in CI; see TestDatabase): the behaviour under test is
/// EF change tracking plus ExecuteDelete, which a mocked repository can't exercise.
/// </summary>
public sealed class TopJokeRepositoryTests : IDisposable
{
    private const string Language = "Ukrainian";

    private readonly TestDatabase database = new();
    // Joke n (1-based) → its id. The database assigns ids: SQL Server refuses explicit identity values.
    private readonly int[] jokeIds;

    public TopJokeRepositoryTests()
    {
        using var context = CreateContext();

        var jokes = Enumerable.Range(1, 4)
            .Select(i => new Joke { Language = Language, Model = "gpt-5.3-chat", Text = $"Joke {i}", GeneratedAt = DateTime.UtcNow })
            .ToList();
        context.Jokes.AddRange(jokes);
        context.SaveChanges();
        jokeIds = jokes.Select(j => j.Id).ToArray();

        context.TopJokes.AddRange(Enumerable.Range(1, 3).Select(i => MakeEntry(rank: i, jokeId: JokeId(i))));
        context.SaveChanges();
    }

    public void Dispose() => database.Dispose();

    private LazyDadDbContext CreateContext() => database.CreateContext();

    private int JokeId(int n) => jokeIds[n - 1];

    private static TopJoke MakeEntry(int rank, int jokeId)
        => new() { Language = Language, Rank = rank, JokeId = jokeId, Reason = "r", JudgeModel = "gpt-6-sol", SelectedAt = DateTime.UtcNow };

    [Fact]
    public async Task ReplaceAsync_AfterReadingCurrentLeaderboardInSameContext_ReplacesRows()
    {
        // Mirrors TopJokeService.UpdateAsync: read the current top, then replace it, in one scope.
        await using (var context = CreateContext())
        {
            var repository = new TopJokeRepository(context);
            var current = await repository.GetByLanguageAsync(Language, CancellationToken.None);
            Assert.Equal(3, current.Count);

            await repository.ReplaceAsync(Language, [MakeEntry(1, JokeId(4)), MakeEntry(2, JokeId(1)), MakeEntry(3, JokeId(2))], CancellationToken.None);
        }

        await using var verify = CreateContext();
        var saved = await new TopJokeRepository(verify).GetByLanguageAsync(Language, CancellationToken.None);
        Assert.Equal([JokeId(4), JokeId(1), JokeId(2)], saved.Select(t => t.JokeId));
        Assert.Equal([1, 2, 3], saved.Select(t => t.Rank));
    }

    [Fact]
    public async Task GetByLanguageAsync_IncludesJokesOrderedByRank()
    {
        await using var context = CreateContext();

        var top = await new TopJokeRepository(context).GetByLanguageAsync(Language, CancellationToken.None);

        Assert.Equal([1, 2, 3], top.Select(t => t.Rank));
        Assert.Equal(["Joke 1", "Joke 2", "Joke 3"], top.Select(t => t.Joke.Text));
    }
}
