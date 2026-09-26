using LazyDad.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Data.Repositories;

public class SchedulerLockRepository : ISchedulerLockRepository
{
    private readonly LazyDadDbContext context;

    public SchedulerLockRepository(LazyDadDbContext context)
    {
        this.context = context;
    }

    public async Task<bool> TryAcquireAsync(string lockKey, string holder, DateTime now, DateTime expiresAt, CancellationToken cancellationToken)
    {
        // One UPDATE: of two replicas racing for an expired lease, the second sees the first's new
        // ExpiresAt when its WHERE is evaluated, so it updates nothing.
        var taken = await context.SchedulerLocks
            .Where(l => l.LockKey == lockKey && l.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.HolderInstanceId, holder)
                .SetProperty(l => l.AcquiredAt, now)
                .SetProperty(l => l.ExpiresAt, expiresAt),
                cancellationToken);
        if (taken == 1)
            return true;

        // No row yet (the first run for this key), or a current lease: possibly our own. If the UPDATE
        // committed but the connection dropped before the count came back, EF's retry updates nothing
        // (the new expiry is in the future), and exactly what we wrote is there.
        var current = await ReadAsync(lockKey, cancellationToken);
        if (current is not null)
            return IsOurs(current, holder, expiresAt);

        return await TryInsertAsync(lockKey, holder, now, expiresAt, cancellationToken);
    }

    public async Task AcquireAsync(string lockKey, string holder, DateTime now, DateTime expiresAt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var updated = await context.SchedulerLocks
                .Where(l => l.LockKey == lockKey)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(l => l.HolderInstanceId, holder)
                    .SetProperty(l => l.AcquiredAt, now)
                    .SetProperty(l => l.ExpiresAt, expiresAt),
                    cancellationToken);
            if (updated == 1 || await TryInsertAsync(lockKey, holder, now, expiresAt, cancellationToken))
                return;
            // Another replica inserted the row between our UPDATE and INSERT: update it this time.
        }
        throw new InvalidOperationException($"Could not acquire scheduler lock '{lockKey}'.");
    }

    /// <summary>
    /// Inserts the lease row. The primary key makes the insert fail for all but one replica: <c>false</c> means
    /// another replica's row is there now. Any other failure (a timeout, a schema problem) is rethrown, so it
    /// fails the tick instead of passing for lost contention.
    /// </summary>
    internal async Task<bool> TryInsertAsync(string lockKey, string holder, DateTime now, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var entry = context.SchedulerLocks.Add(new SchedulerLock { LockKey = lockKey, HolderInstanceId = holder, AcquiredAt = now, ExpiresAt = expiresAt });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Provider-neutral check for a key conflict: the row we failed to insert exists. It's ours if
            // an earlier attempt of this insert committed before a retry hit the key.
            var current = await ReadAsync(lockKey, cancellationToken);
            if (current is not null)
                return IsOurs(current, holder, expiresAt);
            throw;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    private async Task<SchedulerLock?> ReadAsync(string lockKey, CancellationToken cancellationToken)
        => await context.SchedulerLocks.AsNoTracking().SingleOrDefaultAsync(l => l.LockKey == lockKey, cancellationToken);

    // Exactly the lease this call tried to write: same holder and the same expiry (unique per attempt).
    private static bool IsOurs(SchedulerLock lease, string holder, DateTime expiresAt)
        => lease.HolderInstanceId == holder && lease.ExpiresAt == expiresAt;
}
