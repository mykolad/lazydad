using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

public interface ITopJokeRepository
{
    /// <summary>All leaderboard slots with their jokes, ordered by language then rank.</summary>
    Task<List<TopJoke>> GetAllAsync(CancellationToken cancellationToken);
    /// <summary>A language's leaderboard slots with their jokes, best first.</summary>
    Task<List<TopJoke>> GetByLanguageAsync(string language, CancellationToken cancellationToken);
    /// <summary>Atomically replaces a language's whole leaderboard with <paramref name="entries"/>.</summary>
    Task ReplaceAsync(string language, IReadOnlyList<TopJoke> entries, CancellationToken cancellationToken);
}
