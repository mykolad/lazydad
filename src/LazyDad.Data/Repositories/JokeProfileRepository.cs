using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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

    public async Task SaveAllAsync(IReadOnlyList<JokeProfile> profiles, CancellationToken cancellationToken)
    {
        if (profiles.Count == 0)
            return;
        var kinds = profiles.Select(p => p.Kind).Distinct().ToList();
        var ids = profiles.Select(p => p.JokeId).ToList();
        var existing = await context.JokeProfiles
            .Where(p => ids.Contains(p.JokeId) && kinds.Contains(p.Kind))
            .ToDictionaryAsync(p => (p.JokeId, p.Kind), cancellationToken);
        var added = new List<EntityEntry<JokeProfile>>();
        foreach (var profile in profiles)
        {
            if (existing.TryGetValue((profile.JokeId, profile.Kind), out var current))
                Update(current, profile);
            else
                added.Add(context.JokeProfiles.Add(profile));
        }
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A key conflict: another replica saved some of these meanwhile. One by one, each writes over its row.
            foreach (var entry in added)
                entry.State = EntityState.Detached;
            foreach (var profile in profiles)
                await SaveAsync(profile, cancellationToken);
        }
    }

    private static void Update(JokeProfile existing, JokeProfile profile)
    {
        existing.Version = profile.Version;
        existing.Vector = profile.Vector;
        existing.CreatedAt = profile.CreatedAt;
    }
}
