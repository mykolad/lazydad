using LazyDad.Data;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LazyDad.Tests;

/// <summary>Runs against a real database (SQLite in memory, or SQL Server with the migrations in CI; see TestDatabase).</summary>
public sealed class JokeProfileRepositoryTests : IDisposable
{
    private readonly TestDatabase database = new();
    // Joke n (1-based) → its id; joke n is n hours newer than joke 1.
    private readonly int[] jokeIds;

    public JokeProfileRepositoryTests()
    {
        using var context = CreateContext();
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var jokes = Enumerable.Range(1, 4)
            .Select(i => new Joke { Language = "Ukrainian", Model = "gpt-5.3-chat", Text = $"Joke {i}", GeneratedAt = start.AddHours(i) })
            .ToList();
        context.Jokes.AddRange(jokes);
        context.SaveChanges();
        jokeIds = jokes.Select(j => j.Id).ToArray();
    }

    public void Dispose() => database.Dispose();

    private LazyDadDbContext CreateContext() => database.CreateContext();

    private int JokeId(int n) => jokeIds[n - 1];

    private static JokeProfile Profile(int jokeId, string kind, string version, byte[] vector)
        => new() { JokeId = jokeId, Kind = kind, Version = version, Vector = vector, CreatedAt = DateTime.UtcNow };

    [Fact]
    public async Task GetJokesWithoutAsync_ReturnsTheNewestJokesLackingThatKindAndVersion()
    {
        await using (var context = CreateContext())
        {
            var repository = new JokeProfileRepository(context);
            await repository.SaveAsync(Profile(JokeId(4), JokeProfile.JevKind, "v1", [1]), CancellationToken.None);
            // An older version doesn't count: that joke needs profiling again.
            await repository.SaveAsync(Profile(JokeId(3), JokeProfile.JevKind, "v0", [1]), CancellationToken.None);
            // Another kind doesn't count either.
            await repository.SaveAsync(Profile(JokeId(2), JokeProfile.EmbeddingKind, "v1", [1]), CancellationToken.None);
        }

        await using var verify = CreateContext();
        var jokes = await new JokeProfileRepository(verify).GetJokesWithoutAsync(JokeProfile.JevKind, "v1", 2, CancellationToken.None);

        Assert.Equal([JokeId(3), JokeId(2)], jokes.Select(j => j.Id));
    }

    [Fact]
    public async Task SaveAsync_ReplacesTheJokesEarlierProfileOfTheSameKind_AndGetAllAsyncReadsOneVersion()
    {
        await using (var context = CreateContext())
        {
            var repository = new JokeProfileRepository(context);
            await repository.SaveAsync(Profile(JokeId(1), JokeProfile.JevKind, "v0", [1, 2]), CancellationToken.None);
            await repository.SaveAsync(Profile(JokeId(1), JokeProfile.EmbeddingKind, "e1", [9]), CancellationToken.None);
        }
        await using (var context = CreateContext())
            await new JokeProfileRepository(context).SaveAsync(Profile(JokeId(1), JokeProfile.JevKind, "v1", [3, 4]), CancellationToken.None);

        await using var verify = CreateContext();
        var repositoryToVerify = new JokeProfileRepository(verify);
        var jev = Assert.Single(await repositoryToVerify.GetAllAsync(JokeProfile.JevKind, "v1", CancellationToken.None));
        Assert.Equal([3, 4], jev.Vector);
        Assert.Empty(await repositoryToVerify.GetAllAsync(JokeProfile.JevKind, "v0", CancellationToken.None));
        Assert.Single(await repositoryToVerify.GetAllAsync(JokeProfile.EmbeddingKind, "e1", CancellationToken.None));
    }

    [Fact]
    public async Task DeletingAJoke_DeletesItsProfiles()
    {
        await using (var context = CreateContext())
            await new JokeProfileRepository(context).SaveAsync(Profile(JokeId(1), JokeProfile.JevKind, "v1", [1]), CancellationToken.None);

        await using (var context = CreateContext())
        {
            context.Jokes.Remove(await context.Jokes.FindAsync(JokeId(1)) ?? throw new InvalidOperationException());
            await context.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        Assert.Empty(await new JokeProfileRepository(verify).GetAllAsync(JokeProfile.JevKind, "v1", CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_WhenAnotherReplicaInsertsTheSameProfileFirst_WritesOverIt()
    {
        // Just before this context inserts, another one (another replica) inserts the same joke's profile.
        var conflict = new InsertFirst(() => database.CreateContext(), Profile(JokeId(1), JokeProfile.JevKind, "v1", [7]));
        await using (var context = database.CreateContext([conflict]))
            await new JokeProfileRepository(context).SaveAsync(Profile(JokeId(1), JokeProfile.JevKind, "v1", [1, 2]), CancellationToken.None);

        await using var verify = CreateContext();
        Assert.True(conflict.Inserted);
        Assert.Equal([1, 2], Assert.Single(await new JokeProfileRepository(verify).GetAllAsync(JokeProfile.JevKind, "v1", CancellationToken.None)).Vector);
    }

    private sealed class InsertFirst(Func<LazyDadDbContext> otherContext, JokeProfile profile) : SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Inserted)
            {
                Inserted = true;
                await using var other = otherContext();
                other.JokeProfiles.Add(profile);
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
