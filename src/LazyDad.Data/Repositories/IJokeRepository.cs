using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

public interface IJokeRepository
{
    Task<List<Joke>> GetAllAsync(CancellationToken cancellationToken);
    Task<List<Joke>> GetByLanguageAsync(string language, CancellationToken cancellationToken);
    /// <summary>Returns the N most recent jokes for a language, newest first. Used to build the uniqueness prompt.</summary>
    Task<List<Joke>> GetRecentByLanguageAsync(string language, int count, CancellationToken cancellationToken);
    Task<Joke?> GetByIdAsync(int id, CancellationToken cancellationToken);
    /// <summary>Whether a joke with the same text (see <see cref="JokeText"/>) already exists in <paramref name="language"/>.</summary>
    Task<bool> TextExistsAsync(string language, string text, CancellationToken cancellationToken);
    /// <summary>
    /// Deletes the later copies of each joke in <paramref name="language"/> (see <see cref="JokeText"/>), keeping one per
    /// group: the copy in the Top 3, else the most-voted, else the oldest. A Top 3 joke is never deleted, so a group with
    /// several copies in the Top 3 keeps them all. Their profiles and votes go with them. Returns the ids it deleted.
    /// </summary>
    Task<IReadOnlyList<int>> RemoveDuplicatesAsync(string language, CancellationToken cancellationToken);
    /// <summary>Which of <paramref name="ids"/> are still jokes.</summary>
    Task<List<int>> GetExistingIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken);
    Task AddAsync(Joke joke, CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
    /// <summary>
    /// One page of all jokes in <paramref name="sort"/> order (ties broken by id), starting after
    /// <paramref name="after"/>, or at the top when it's <c>null</c>.
    /// </summary>
    Task<List<Joke>> GetPageAsync(JokeSort sort, JokeCursor? after, int limit, CancellationToken cancellationToken);
    /// <summary>
    /// Atomically adds the deltas to a joke's vote counts (never below zero) and returns the joke
    /// with its new counts, or <c>null</c> if it doesn't exist.
    /// </summary>
    Task<Joke?> AddVotesAsync(int id, int upDelta, int downDelta, CancellationToken cancellationToken);
}
