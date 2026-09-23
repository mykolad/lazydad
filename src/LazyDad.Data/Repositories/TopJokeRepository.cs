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

    public async Task<List<TopJoke>> GetAllAsync(CancellationToken cancellationToken)
        => await context.TopJokes
            .Include(t => t.Joke)
            .OrderBy(t => t.Language)
            .ThenBy(t => t.Rank)
            .ToListAsync(cancellationToken);

    public async Task<List<TopJoke>> GetByLanguageAsync(string language, CancellationToken cancellationToken)
        => await context.TopJokes
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

            context.TopJokes.AddRange(entries);
            await context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });
    }
}
