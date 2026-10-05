using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

/// <summary>Signed-in votes (see <see cref="Vote"/>). A joke's <c>Up</c>/<c>Down</c> change only together with its vote rows.</summary>
public interface IVoteRepository
{
    /// <summary>
    /// Sets the voter's vote on a joke to <paramref name="value"/> (1, -1, or 0 to remove it) and adjusts the joke's
    /// counts in the same transaction. Setting the vote the voter already has changes nothing, so a repeated or retried
    /// request is harmless. Returns the joke with its counts, or <c>null</c> if it doesn't exist.
    /// </summary>
    Task<Joke?> SetAsync(int jokeId, byte[] voterKey, int value, DateTime now, CancellationToken cancellationToken);

    /// <summary>The voter's votes on these jokes, by joke id (jokes without a vote are left out).</summary>
    Task<IReadOnlyDictionary<int, int>> GetAsync(byte[] voterKey, IReadOnlyCollection<int> jokeIds, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all of the voter's votes and takes them off the jokes' counts. Returns no count: a retry after a commit
    /// whose answer was lost finds nothing left and would report 0.
    /// </summary>
    Task DeleteAllAsync(byte[] voterKey, CancellationToken cancellationToken);
}
