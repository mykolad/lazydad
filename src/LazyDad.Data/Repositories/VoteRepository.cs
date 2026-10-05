using System.Data;
using System.Data.Common;
using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data.Repositories;

public class VoteRepository : IVoteRepository
{
    // How often SetAsync reads the vote again after another request changed it between the read and the write. Only
    // the same voter's requests race for a row (a double click, two tabs), so a second try nearly always settles it.
    internal const int MaxAttempts = 3;

    private readonly LazyDadDbContext context;

    public VoteRepository(LazyDadDbContext context)
    {
        this.context = context;
    }

    public async Task<Joke?> SetAsync(int jokeId, byte[] voterKey, int value, DateTime now, CancellationToken cancellationToken)
    {
        if (value is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "A vote is -1, 0 or 1.");
        CheckVoterKey(voterKey);

        // The execution strategy (EnableRetryOnFailure) runs the whole attempt again after a transient error, also one
        // that hid a commit: that run finds the vote already set and changes nothing.
        var strategy = context.Database.CreateExecutionStrategy();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (settled, joke) = await strategy.ExecuteAsync(
                token => TrySetAsync(jokeId, voterKey, (short)value, now, token), cancellationToken);
            if (settled)
                return joke;
        }
        throw new InvalidOperationException($"The vote on joke {jokeId} kept changing while it was being set.");
    }

    // Reads the voter's current vote, then changes the row only if it still holds that vote (or, for a new vote, if
    // there's still none: the primary key), and the counts by the difference, in one transaction. If another request
    // changed the row in between, nothing is written and the caller reads again, so the counts never move twice.
    private async Task<(bool Settled, Joke? Joke)> TrySetAsync(int jokeId, byte[] voterKey, short value, DateTime now, CancellationToken cancellationToken)
    {
        if (!await context.Jokes.AnyAsync(j => j.Id == jokeId, cancellationToken))
            return (true, null);

        var current = await context.Votes
            .Where(v => v.JokeId == jokeId && v.VoterKey == voterKey)
            .Select(v => (short?)v.Value)
            .SingleOrDefaultAsync(cancellationToken) ?? (short)0;
        if (current == value)
            return (true, await ReadJokeAsync(jokeId, cancellationToken));

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            var written = current == 0
                ? await TryInsertAsync(jokeId, voterKey, value, now, cancellationToken)
                : await ChangeAsync(jokeId, voterKey, current, value, now, cancellationToken);
            if (!written)
                return (false, null);

            await AddToCountsAsync(jokeId, Count(value, 1) - Count(current, 1), Count(value, -1) - Count(current, -1), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return (true, await ReadJokeAsync(jokeId, cancellationToken));
    }

    // Plain SQL rather than Add + SaveChanges: SaveChanges would also save whatever else the caller's context has
    // pending, inside this transaction. Like the other writes here, it leaves the change tracker alone.
    private async Task<bool> TryInsertAsync(int jokeId, byte[] voterKey, short value, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO Votes (JokeId, VoterKey, Value, UpdatedAt) VALUES ({jokeId}, {voterKey}, {value}, {now})",
                cancellationToken);
            return true;
        }
        catch (DbException)
        {
            // The primary key, if the same voter's other request inserted first: then its row is there, and the caller
            // reads again. Anything else (a transient error, for the execution strategy to retry) goes on up.
            if (await context.Votes.AnyAsync(v => v.JokeId == jokeId && v.VoterKey == voterKey, cancellationToken))
                return false;
            throw;
        }
    }

    private async Task<bool> ChangeAsync(int jokeId, byte[] voterKey, short current, short value, DateTime now, CancellationToken cancellationToken)
    {
        var row = context.Votes.Where(v => v.JokeId == jokeId && v.VoterKey == voterKey && v.Value == current);
        var changed = value == 0
            ? await row.ExecuteDeleteAsync(cancellationToken)
            : await row.ExecuteUpdateAsync(setters => setters
                .SetProperty(v => v.Value, value)
                .SetProperty(v => v.UpdatedAt, now),
                cancellationToken);
        return changed == 1;
    }

    // One UPDATE, so concurrent votes from different voters can't overwrite each other's counts.
    private Task<int> AddToCountsAsync(int jokeId, int upDelta, int downDelta, CancellationToken cancellationToken)
        => context.Jokes
            .Where(j => j.Id == jokeId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Up, j => j.Up + upDelta < 0 ? 0 : j.Up + upDelta)
                .SetProperty(j => j.Down, j => j.Down + downDelta < 0 ? 0 : j.Down + downDelta),
                cancellationToken);

    private Task<Joke> ReadJokeAsync(int jokeId, CancellationToken cancellationToken)
        => context.Jokes.AsNoTracking().SingleAsync(j => j.Id == jokeId, cancellationToken);

    private static int Count(short vote, short side) => vote == side ? 1 : 0;

    public async Task<IReadOnlyDictionary<int, int>> GetAsync(byte[] voterKey, IReadOnlyCollection<int> jokeIds, CancellationToken cancellationToken)
    {
        CheckVoterKey(voterKey);
        if (jokeIds.Count == 0)
            return new Dictionary<int, int>();

        return await context.Votes
            .AsNoTracking()
            .Where(v => v.VoterKey == voterKey && jokeIds.Contains(v.JokeId))
            .ToDictionaryAsync(v => v.JokeId, v => (int)v.Value, cancellationToken);
    }

    public async Task DeleteAllAsync(byte[] voterKey, CancellationToken cancellationToken)
    {
        CheckVoterKey(voterKey);

        // Serializable: the voter's own vote that lands meanwhile waits until this commits (and then counts on its own),
        // instead of being deleted after the counts were taken. Rare, so the stricter locks cost nothing that matters.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var votes = context.Votes.Where(v => v.VoterKey == voterKey);
            await context.Jokes
                .Where(j => votes.Any(v => v.JokeId == j.Id && v.Value == 1))
                .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Up, j => j.Up > 0 ? j.Up - 1 : 0), token);
            await context.Jokes
                .Where(j => votes.Any(v => v.JokeId == j.Id && v.Value == -1))
                .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Down, j => j.Down > 0 ? j.Down - 1 : 0), token);
            await votes.ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
        }, cancellationToken);
    }

    private static void CheckVoterKey(byte[] voterKey)
    {
        if (voterKey.Length != Vote.VoterKeyLength)
            throw new ArgumentException($"A voter key is {Vote.VoterKeyLength} bytes (was {voterKey.Length}).", nameof(voterKey));
    }
}
