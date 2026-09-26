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

    public TopJokeRepositoryTests()
    {
        using var context = CreateContext();

        context.Jokes.AddRange(Enumerable.Range(1, 4).Select(i => new Joke
        {
            Id = i, Language = Language, Model = "gpt-5.3-chat", Text = $"Joke {i}", GeneratedAt = DateTime.UtcNow
        }));
        context.TopJokes.AddRange(Enumerable.Range(1, 3).Select(i => MakeEntry(rank: i, jokeId: i)));
        context.SaveChanges();
    }

    public void Dispose() => database.Dispose();

    private LazyDadDbContext CreateContext() => database.CreateContext();

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

            await repository.ReplaceAsync(Language, [MakeEntry(1, 4), MakeEntry(2, 1), MakeEntry(3, 2)], CancellationToken.None);
        }

        await using var verify = CreateContext();
        var saved = await new TopJokeRepository(verify).GetByLanguageAsync(Language, CancellationToken.None);
        Assert.Equal([4, 1, 2], saved.Select(t => t.JokeId));
        Assert.Equal([1, 2, 3], saved.Select(t => t.Rank));
    }

    [Fact]
    public async Task GetByLanguageAsync_IncludesJokesOrderedByRank()
    {
        await using var context = CreateContext();

        var top = await new TopJokeRepository(context).GetByLanguageAsync(Language, CancellationToken.None);

        Assert.Equal([1, 2, 3], top.Select(t => t.Rank));
        Assert.All(top, t => Assert.Equal($"Joke {t.JokeId}", t.Joke.Text));
    }
}
