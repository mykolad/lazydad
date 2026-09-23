using LazyDad.Data;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Tests;

/// <summary>Runs against a real (SQLite in-memory) database so the LINQ queries are actually translated and executed.</summary>
public sealed class JokeRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public JokeRepositoryTests()
    {
        connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();

        context.Jokes.AddRange(
            new Joke { Language = "Ukrainian", Model = "m", Text = "uk-old", GeneratedAt = Now.AddHours(-8) },
            new Joke { Language = "Ukrainian", Model = "m", Text = "uk-new", GeneratedAt = Now },
            new Joke { Language = "Ukrainian", Model = "m", Text = "uk-mid", GeneratedAt = Now.AddHours(-4) },
            new Joke { Language = "English", Model = "m", Text = "en", GeneratedAt = Now.AddHours(-2) });
        context.SaveChanges();
    }

    public void Dispose() => connection.Dispose();

    private LazyDadDbContext CreateContext()
        => new(new DbContextOptionsBuilder<LazyDadDbContext>().UseSqlite(connection).Options);

    [Fact]
    public async Task GetAllAsync_ReturnsAllJokesNewestFirst()
    {
        await using var context = CreateContext();

        var jokes = await new JokeRepository(context).GetAllAsync(CancellationToken.None);

        Assert.Equal(["uk-new", "en", "uk-mid", "uk-old"], jokes.Select(j => j.Text));
    }

    [Fact]
    public async Task GetByLanguageAsync_FiltersByLanguageNewestFirst()
    {
        await using var context = CreateContext();

        var jokes = await new JokeRepository(context).GetByLanguageAsync("Ukrainian", CancellationToken.None);

        Assert.Equal(["uk-new", "uk-mid", "uk-old"], jokes.Select(j => j.Text));
    }

    [Fact]
    public async Task GetRecentByLanguageAsync_ReturnsRequestedCountNewestFirst()
    {
        await using var context = CreateContext();

        var jokes = await new JokeRepository(context).GetRecentByLanguageAsync("Ukrainian", 2, CancellationToken.None);

        Assert.Equal(["uk-new", "uk-mid"], jokes.Select(j => j.Text));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsJokeOrNull()
    {
        await using var context = CreateContext();
        var repository = new JokeRepository(context);
        var existing = (await repository.GetAllAsync(CancellationToken.None))[0];

        Assert.Equal(existing.Text, (await repository.GetByIdAsync(existing.Id, CancellationToken.None))?.Text);
        Assert.Null(await repository.GetByIdAsync(-1, CancellationToken.None));
    }

    [Fact]
    public async Task AddAsync_PersistsJokeAndAssignsId()
    {
        var joke = new Joke { Language = "Ukrainian", Model = "gpt-6-luna", Text = "added", GeneratedAt = Now.AddHours(1) };

        await using (var context = CreateContext())
            await new JokeRepository(context).AddAsync(joke, CancellationToken.None);

        Assert.True(joke.Id > 0);
        await using var verify = CreateContext();
        Assert.Equal("added", (await new JokeRepository(verify).GetByIdAsync(joke.Id, CancellationToken.None))?.Text);
    }
}
