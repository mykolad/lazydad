using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data.Repositories;

public class JokeProfileRepository : IJokeProfileRepository
{
    private readonly LazyDadDbContext context;

    public JokeProfileRepository(LazyDadDbContext context)
    {
        this.context = context;
    }

    public async Task<List<JokeProfile>> GetAllAsync(string kind, string version, CancellationToken cancellationToken)
        => await context.JokeProfiles
            .Where(p => p.Kind == kind && p.Version == version)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<List<Joke>> GetJokesWithoutAsync(string kind, string version, int limit, CancellationToken cancellationToken)
        => await context.Jokes
            .Where(j => !context.JokeProfiles.Any(p => p.JokeId == j.Id && p.Kind == kind && p.Version == version))
            .OrderByDescending(j => j.GeneratedAt)
            .ThenByDescending(j => j.Id)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(JokeProfile profile, CancellationToken cancellationToken)
    {
        var existing = await context.JokeProfiles.FindAsync([profile.JokeId, profile.Kind], cancellationToken);
        if (existing is null)
        {
            context.JokeProfiles.Add(profile);
        }
        else
        {
            existing.Version = profile.Version;
            existing.Vector = profile.Vector;
            existing.CreatedAt = profile.CreatedAt;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
