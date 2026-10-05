using System.Data.Common;
using LazyDad.Data;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LazyDad.Tests;

/// <summary>Runs against a real database (SQLite in memory, or SQL Server with the migrations in CI; see TestDatabase).</summary>
public sealed class VoteRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Alice = Key(1);
    private static readonly byte[] Bob = Key(2);

    private readonly TestDatabase database = new();
    private readonly int jokeId;
    private readonly int otherJokeId;

    public VoteRepositoryTests()
    {
        using var context = database.CreateContext();
        var joke = new Joke { Language = "Ukrainian", Model = "m", Text = "joke", GeneratedAt = Now };
        var other = new Joke { Language = "Ukrainian", Model = "m", Text = "other", GeneratedAt = Now };
        context.Jokes.AddRange(joke, other);
        context.SaveChanges();
        jokeId = joke.Id;
        otherJokeId = other.Id;
    }

    public void Dispose() => database.Dispose();

    private static byte[] Key(byte seed) => Enumerable.Repeat(seed, Vote.VoterKeyLength).ToArray();

    private async Task<Joke?> SetAsync(int id, byte[] voter, int value)
    {
        await using var context = database.CreateContext();
        return await new VoteRepository(context).SetAsync(id, voter, value, Now, CancellationToken.None);
    }

    // The counts and the rows, read fresh: they must always agree.
    private async Task AssertStateAsync(int id, int up, int down, int[] votes)
    {
        await using var context = database.CreateContext();
        var joke = await context.Jokes.SingleAsync(j => j.Id == id);
        var stored = await context.Votes.Where(v => v.JokeId == id).Select(v => (int)v.Value).ToListAsync();
        Assert.Equal((up, down), (joke.Up, joke.Down));
        Assert.Equal(votes, stored.Order());
    }

    [Fact]
    public async Task SetAsync_ANewVote_IsStoredAndCounted()
    {
        var joke = await SetAsync(jokeId, Alice, 1);

        Assert.Equal((1, 0), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 1, 0, [1]);
    }

    [Fact]
    public async Task SetAsync_TheSameVoteAgain_ChangesNothing()
    {
        await SetAsync(jokeId, Alice, -1);

        var joke = await SetAsync(jokeId, Alice, -1);

        Assert.Equal((0, 1), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 0, 1, [-1]);
    }

    [Fact]
    public async Task SetAsync_SwitchingAVote_MovesItsCount()
    {
        await SetAsync(jokeId, Alice, 1);

        var joke = await SetAsync(jokeId, Alice, -1);

        Assert.Equal((0, 1), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 0, 1, [-1]);
    }

    [Fact]
    public async Task SetAsync_Zero_RemovesTheVote()
    {
        await SetAsync(jokeId, Alice, 1);

        var joke = await SetAsync(jokeId, Alice, 0);

        Assert.Equal((0, 0), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 0, 0, []);
    }

    [Fact]
    public async Task SetAsync_ZeroWithoutAVote_ReturnsTheCounts()
    {
        await SetAsync(jokeId, Bob, 1);

        var joke = await SetAsync(jokeId, Alice, 0);

        Assert.Equal((1, 0), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 1, 0, [1]);
    }

    [Fact]
    public async Task SetAsync_VotesOfDifferentVoters_AllCount()
    {
        await SetAsync(jokeId, Alice, 1);
        await SetAsync(jokeId, Bob, 1);
        await SetAsync(otherJokeId, Alice, -1);

        await AssertStateAsync(jokeId, 2, 0, [1, 1]);
        await AssertStateAsync(otherJokeId, 0, 1, [-1]);
    }

    [Fact]
    public async Task SetAsync_SeveralTimesInOneContext_KeepsItsOtherChanges()
    {
        // One request (one scoped context): add, remove, add again, with an unrelated change pending.
        await using var context = database.CreateContext();
        var repository = new VoteRepository(context);
        var pending = context.Jokes.Add(new Joke { Language = "Ukrainian", Model = "m", Text = "pending", GeneratedAt = Now });

        await repository.SetAsync(jokeId, Alice, 1, Now, CancellationToken.None);
        await repository.SetAsync(jokeId, Alice, 0, Now, CancellationToken.None);
        var joke = await repository.SetAsync(jokeId, Alice, 1, Now, CancellationToken.None);

        Assert.Equal((1, 0), (joke!.Up, joke.Down));
        await AssertStateAsync(jokeId, 1, 0, [1]);
        // Neither lost nor saved along with the votes: still the caller's to save.
        Assert.Equal(EntityState.Added, pending.State);
        await using var fresh = database.CreateContext();
        Assert.False(await fresh.Jokes.AnyAsync(j => j.Text == "pending"));
    }

    [Fact]
    public async Task SetAsync_AnUnknownJoke_ReturnsNullAndStoresNothing()
    {
        Assert.Null(await SetAsync(jokeId + otherJokeId + 1, Alice, 1));

        await using var context = database.CreateContext();
        Assert.Empty(await context.Votes.ToListAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task SetAsync_RejectsAValueThatIsNotAVote(int value)
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SetAsync(jokeId, Alice, value));

    [Fact]
    public async Task SetAsync_RejectsAKeyOfTheWrongLength()
        => await Assert.ThrowsAsync<ArgumentException>(() => SetAsync(jokeId, new byte[16], 1));

    [Fact]
    public async Task SetAsync_WhenTheSameVoterInsertsFirst_CountsTheVoteOnce()
    {
        // A double click: the other request inserts the same vote between this one's read and its insert.
        var race = new SetFirst(() => database.CreateContext(), jokeId, Alice, 1);
        await using (var context = database.CreateContext([race]))
            await new VoteRepository(context).SetAsync(jokeId, Alice, 1, Now, CancellationToken.None);

        Assert.True(race.Ran);
        await AssertStateAsync(jokeId, 1, 0, [1]);
    }

    [Fact]
    public async Task SetAsync_WhenTheSameVoterChangesTheVoteFirst_ReadsItAgainAndKeepsTheCountsRight()
    {
        // Two tabs: the other request switches the vote between this one's read and its write. This one is the last,
        // so its vote stays, counted once.
        await SetAsync(jokeId, Alice, 1);
        var race = new SetFirst(() => database.CreateContext(), jokeId, Alice, -1);
        await using (var context = database.CreateContext([race]))
            await new VoteRepository(context).SetAsync(jokeId, Alice, 0, Now, CancellationToken.None);

        Assert.True(race.Ran);
        await AssertStateAsync(jokeId, 0, 0, []);
    }

    [Fact]
    public async Task SetAsync_WhenTheInsertFailsForAnotherReason_Throws()
    {
        // Not a race (no row appeared): the error must reach the caller, or the execution strategy, as it is.
        var failure = new FailingInsert();
        await using (var context = database.CreateContext([failure]))
            await Assert.ThrowsAsync<SqliteException>(
                () => new VoteRepository(context).SetAsync(jokeId, Alice, 1, Now, CancellationToken.None));

        Assert.True(failure.Failed);
        await AssertStateAsync(jokeId, 0, 0, []);
    }

    [Fact]
    public async Task GetAsync_ReturnsTheVotersVotesOnTheseJokes()
    {
        await SetAsync(jokeId, Alice, 1);
        await SetAsync(otherJokeId, Alice, -1);
        await SetAsync(otherJokeId, Bob, 1);

        await using var context = database.CreateContext();
        var repository = new VoteRepository(context);

        Assert.Equal(new Dictionary<int, int> { [jokeId] = 1, [otherJokeId] = -1 },
            await repository.GetAsync(Alice, [jokeId, otherJokeId], CancellationToken.None));
        Assert.Equal(new Dictionary<int, int> { [otherJokeId] = 1 },
            await repository.GetAsync(Bob, [jokeId, otherJokeId], CancellationToken.None));
        Assert.Empty(await repository.GetAsync(Alice, [], CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAllAsync_DeletesOnlyTheVotersVotesAndTheirCounts()
    {
        await SetAsync(jokeId, Alice, 1);
        await SetAsync(otherJokeId, Alice, -1);
        await SetAsync(jokeId, Bob, 1);

        await using (var context = database.CreateContext())
            await new VoteRepository(context).DeleteAllAsync(Alice, CancellationToken.None);

        await AssertStateAsync(jokeId, 1, 0, [1]);
        await AssertStateAsync(otherJokeId, 0, 0, []);
    }

    [Fact]
    public async Task DeleteAllAsync_WithoutVotes_DeletesNothing()
    {
        await SetAsync(jokeId, Bob, -1);

        await using (var context = database.CreateContext())
            await new VoteRepository(context).DeleteAllAsync(Alice, CancellationToken.None);

        await AssertStateAsync(jokeId, 0, 1, [-1]);
    }

    private sealed class FailingInsert : DbCommandInterceptor
    {
        public bool Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
        {
            if (command.CommandText.StartsWith("INSERT INTO Votes", StringComparison.Ordinal))
            {
                Failed = true;
                throw new SqliteException("A failure that isn't a key conflict.", 1);
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Plays the same voter's other request: just before this one's transaction starts (after it read the vote), a
    /// separate repository sets the vote and commits.
    /// </summary>
    private sealed class SetFirst(Func<LazyDadDbContext> otherContext, int jokeId, byte[] voterKey, int value) : DbTransactionInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken)
        {
            if (!Ran)
            {
                Ran = true;
                await using var other = otherContext();
                await new VoteRepository(other).SetAsync(jokeId, voterKey, value, Now, cancellationToken);
            }
            return result;
        }
    }
}
