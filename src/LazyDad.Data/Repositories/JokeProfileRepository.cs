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
        if (existing is not null)
        {
            Update(existing, profile);
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var entry = context.JokeProfiles.Add(profile);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The primary key: another replica saved this joke's profile in the meantime. Write ours over it; if the row
            // isn't there, the failure was something else.
            entry.State = EntityState.Detached;
            var current = await context.JokeProfiles.FindAsync([profile.JokeId, profile.Kind], cancellationToken);
            if (current is null)
                throw;
            Update(current, profile);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private static void Update(JokeProfile existing, JokeProfile profile)
    {
        existing.Version = profile.Version;
        existing.Vector = profile.Vector;
        existing.CreatedAt = profile.CreatedAt;
    }
}
