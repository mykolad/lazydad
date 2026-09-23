using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data.Repositories;

public class TopJokeRepository : ITopJokeRepository
{
    private readonly LazyDadDbContext context;

    public TopJokeRepository(LazyDadDbContext context)
    {
        this.context = context;
    }

    // Reads are no-tracking: ReplaceAsync inserts rows with the same (Language, Rank) keys,
    // which EF refuses to track while the previously read instances are still tracked.
    public async Task<List<TopJoke>> GetAllAsync(CancellationToken cancellationToken)
        => await context.TopJokes
            .AsNoTracking()
            .Include(t => t.Joke)
            .OrderBy(t => t.Language)
            .ThenBy(t => t.Rank)
            .ToListAsync(cancellationToken);

    public async Task<List<TopJoke>> GetByLanguageAsync(string language, CancellationToken cancellationToken)
        => await context.TopJokes
            .AsNoTracking()
            .Include(t => t.Joke)
            .Where(t => t.Language == language)
            .OrderBy(t => t.Rank)
            .ToListAsync(cancellationToken);

    public async Task ReplaceAsync(string language, IReadOnlyList<TopJoke> entries, CancellationToken cancellationToken)
    {
        // Delete + insert rather than update-in-place: reshuffling jokes between ranks
        // would transiently violate the unique JokeId index within a single SaveChanges.
        // The retrying execution strategy requires user transactions to run inside it.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            await context.TopJokes
                .Where(t => t.Language == language)
                .ExecuteDeleteAsync(cancellationToken);

            // ExecuteDelete bypasses the change tracker; drop any rows still tracked for this
            // language (e.g. loaded by a caller with tracking) so the inserts don't collide.
            foreach (var stale in context.ChangeTracker.Entries<TopJoke>().Where(e => e.Entity.Language == language).ToList())
                stale.State = EntityState.Detached;

            context.TopJokes.AddRange(entries);
            await context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
