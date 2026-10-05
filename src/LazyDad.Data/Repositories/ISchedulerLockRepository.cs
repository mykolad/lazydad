namespace LazyDad.Data.Repositories;

/// <summary>
/// Leases in the <c>SchedulerLocks</c> table: which replica may run a scheduled job until when.
/// </summary>
public interface ISchedulerLockRepository
{
    /// <summary>
    /// Takes <paramref name="lockKey"/> for <paramref name="holder"/> until <paramref name="expiresAt"/> if nobody
    /// holds it at <paramref name="now"/> (no row, or the lease has expired). Atomic across replicas: at most one wins.
    /// </summary>
    Task<bool> TryAcquireAsync(string lockKey, string holder, DateTime now, DateTime expiresAt, CancellationToken cancellationToken);

    /// <summary>Takes <paramref name="lockKey"/> for <paramref name="holder"/> until <paramref name="expiresAt"/>, even from a current holder.</summary>
    Task AcquireAsync(string lockKey, string holder, DateTime now, DateTime expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// Ends <paramref name="holder"/>'s lease on <paramref name="lockKey"/> at <paramref name="now"/>, so the next replica
    /// can take it at once. Does nothing if another replica holds it now.
    /// </summary>
    Task ReleaseAsync(string lockKey, string holder, DateTime now, CancellationToken cancellationToken);
}
