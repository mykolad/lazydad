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
}
