using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data.Repositories;

public class JokeRepository : IJokeRepository
{
    private readonly LazyDadDbContext context;

    public JokeRepository(LazyDadDbContext context)
    {
        this.context = context;
    }

    public async Task<List<Joke>> GetAllAsync(CancellationToken cancellationToken)
        => await context.Jokes
            .OrderByDescending(j => j.GeneratedAt)
            .ToListAsync(cancellationToken);

    public async Task<List<Joke>> GetByLanguageAsync(string language, CancellationToken cancellationToken)
        => await context.Jokes
            .Where(j => j.Language == language)
            .OrderByDescending(j => j.GeneratedAt)
            .ToListAsync(cancellationToken);

    public async Task<List<Joke>> GetRecentByLanguageAsync(string language, int count, CancellationToken cancellationToken)
        => await context.Jokes
            .Where(j => j.Language == language)
            .OrderByDescending(j => j.GeneratedAt)
            .Take(count)
            .ToListAsync(cancellationToken);

    public async Task<Joke?> GetByIdAsync(int id, CancellationToken cancellationToken)
        => await context.Jokes.FindAsync([id], cancellationToken);

    public async Task AddAsync(Joke joke, CancellationToken cancellationToken)
    {
        context.Jokes.Add(joke);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
        => await context.Jokes.CountAsync(cancellationToken);

    public async Task<List<Joke>> GetPageAsync(JokeSort sort, JokeCursor? after, int limit, CancellationToken cancellationToken)
    {
        IQueryable<Joke> jokes = context.Jokes;

        // Keyset pagination: everything strictly after the cursor in the page's own order.
        if (after is not null)
        {
            var (score, generatedAt, id) = (after.Score, after.GeneratedAt, after.Id);
            jokes = sort == JokeSort.TopVoted
                ? jokes.Where(j => j.Up - j.Down < score
                    || (j.Up - j.Down == score && (j.GeneratedAt < generatedAt || (j.GeneratedAt == generatedAt && j.Id < id))))
                : jokes.Where(j => j.GeneratedAt < generatedAt || (j.GeneratedAt == generatedAt && j.Id < id));
        }

        var ordered = sort == JokeSort.TopVoted
            ? jokes.OrderByDescending(j => j.Up - j.Down).ThenByDescending(j => j.GeneratedAt)
            : jokes.OrderByDescending(j => j.GeneratedAt);

        return await ordered
            .ThenByDescending(j => j.Id)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Joke?> AddVotesAsync(int id, int upDelta, int downDelta, CancellationToken cancellationToken)
    {
        // One UPDATE statement, so concurrent votes can't overwrite each other.
        var updated = await context.Jokes
            .Where(j => j.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Up, j => j.Up + upDelta < 0 ? 0 : j.Up + upDelta)
                .SetProperty(j => j.Down, j => j.Down + downDelta < 0 ? 0 : j.Down + downDelta),
                cancellationToken);

        return updated == 0
            ? null
            : await context.Jokes.AsNoTracking().SingleAsync(j => j.Id == id, cancellationToken);
    }
}
