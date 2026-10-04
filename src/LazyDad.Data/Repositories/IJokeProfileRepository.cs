using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

public interface IJokeProfileRepository
{
    /// <summary>Every profile of one method and version (the ones that can be compared with each other).</summary>
    Task<List<JokeProfile>> GetAllAsync(string kind, string version, CancellationToken cancellationToken);

    /// <summary>
    /// Up to <paramref name="limit"/> jokes, newest first, that have no profile of <paramref name="kind"/> in
    /// <paramref name="version"/> yet (none at all, or an older version).
    /// </summary>
    Task<List<Joke>> GetJokesWithoutAsync(string kind, string version, int limit, CancellationToken cancellationToken);

    /// <summary>Saves a joke's profile, replacing any earlier one of the same method.</summary>
    Task SaveAsync(JokeProfile profile, CancellationToken cancellationToken);
}
