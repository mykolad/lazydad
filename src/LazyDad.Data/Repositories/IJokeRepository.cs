using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

public interface IJokeRepository
{
    Task<List<Joke>> GetAllAsync(CancellationToken cancellationToken);
    Task<List<Joke>> GetByLanguageAsync(string language, CancellationToken cancellationToken);
    /// <summary>Returns the N most recent jokes for a language, newest first. Used to build the uniqueness prompt.</summary>
    Task<List<Joke>> GetRecentByLanguageAsync(string language, int count, CancellationToken cancellationToken);
    Task<Joke?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task AddAsync(Joke joke, CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
    /// <summary>One page of all jokes in <paramref name="sort"/> order (ties broken by id, so pages never overlap).</summary>
    Task<List<Joke>> GetPageAsync(JokeSort sort, int offset, int limit, CancellationToken cancellationToken);
    /// <summary>
    /// Atomically adds the deltas to a joke's vote counts (never below zero) and returns the joke
    /// with its new counts, or <c>null</c> if it doesn't exist.
    /// </summary>
    Task<Joke?> AddVotesAsync(int id, int upDelta, int downDelta, CancellationToken cancellationToken);
}
