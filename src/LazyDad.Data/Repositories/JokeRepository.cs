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

    public async Task<bool> TextExistsAsync(string language, string text, CancellationToken cancellationToken)
    {
        // The comparison ignores punctuation and case, which SQL can't express simply; a language's texts are a few
        // hundred kilobytes, read once per generated joke.
        var key = JokeText.Key(text);
        var texts = await context.Jokes.Where(j => j.Language == language).Select(j => j.Text).ToListAsync(cancellationToken);
        return texts.Any(t => JokeText.Key(t) == key);
    }

    public async Task<IReadOnlyList<int>> RemoveDuplicatesAsync(string language, CancellationToken cancellationToken)
    {
        var jokes = await context.Jokes
            .Where(j => j.Language == language)
            .Select(j => new { j.Id, j.Text, j.GeneratedAt, Votes = j.Up + j.Down })
            .ToListAsync(cancellationToken);
        var top = (await context.TopJokes.Select(t => t.JokeId).ToListAsync(cancellationToken)).ToHashSet();

        var copies = jokes
            .GroupBy(j => JokeText.Key(j.Text))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g
                .OrderByDescending(j => top.Contains(j.Id))
                .ThenByDescending(j => j.Votes)
                .ThenBy(j => j.GeneratedAt)
                .ThenBy(j => j.Id)
                .Skip(1))
            // Deleting a Top 3 joke would delete its slot too (cascade): a second copy in the Top 3 stays.
            .Where(j => !top.Contains(j.Id))
            .ToList();
        if (copies.Count == 0)
            return [];

        // What was read is checked again in the delete itself, so a change since then spares the copy: another replica's
        // tick may have promoted it to the Top 3 (deleting it would delete its slot), or a vote may have made it the most
        // voted. So each copy goes only while its vote count is still the one read (one statement per count; almost always
        // just 0). Profiles and votes are deleted with them (cascade).
        foreach (var sameVotes in copies.GroupBy(c => c.Votes))
        {
            var ids = sameVotes.Select(c => c.Id).ToList();
            var votes = sameVotes.Key;
            await context.Jokes
                .Where(j => ids.Contains(j.Id) && j.Up + j.Down == votes && !context.TopJokes.Any(t => t.JokeId == j.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
        var copyIds = copies.Select(c => c.Id).ToList();
        var kept = await context.Jokes.Where(j => copyIds.Contains(j.Id)).Select(j => j.Id).ToListAsync(cancellationToken);
        return copyIds.Except(kept).ToList();
    }

    public async Task<List<int>> GetExistingIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        => ids.Count == 0 ? [] : await context.Jokes.Where(j => ids.Contains(j.Id)).Select(j => j.Id).ToListAsync(cancellationToken);

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
