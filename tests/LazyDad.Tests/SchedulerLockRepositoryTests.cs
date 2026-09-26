using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LazyDad.Tests;

/// <summary>Runs against a real database (SQLite in memory, or SQL Server with the migrations in CI; see TestDatabase), so the atomic UPDATE and the key conflict are real.</summary>
public sealed class SchedulerLockRepositoryTests : IDisposable
{
    private const string Key = "jokes:Ukrainian";
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase database = new();

    public void Dispose() => database.Dispose();

    private LazyDadDbContext CreateContext() => database.CreateContext();

    private async Task<bool> TryAcquireAsync(string holder, DateTime now, DateTime expiresAt)
    {
        await using var context = CreateContext();
        return await new SchedulerLockRepository(context).TryAcquireAsync(Key, holder, now, expiresAt, CancellationToken.None);
    }

    private async Task<(string Holder, DateTime ExpiresAt)> LeaseAsync()
    {
        await using var context = CreateContext();
        var lease = await context.SchedulerLocks.SingleAsync(l => l.LockKey == Key);
        return (lease.HolderInstanceId, lease.ExpiresAt);
    }

    [Fact]
    public async Task TryAcquire_WithoutARow_TakesTheLease()
    {
        Assert.True(await TryAcquireAsync("a", Now, Now.AddHours(4)));

        Assert.Equal(("a", Now.AddHours(4)), await LeaseAsync());
    }

    [Fact]
    public async Task TryAcquire_WhileAnotherHoldsIt_Fails()
    {
        await TryAcquireAsync("a", Now, Now.AddHours(4));

        Assert.False(await TryAcquireAsync("b", Now.AddHours(1), Now.AddHours(5)));
        Assert.Equal("a", (await LeaseAsync()).Holder);
    }

    [Fact]
    public async Task TryAcquire_AfterItExpired_TakesItOver()
    {
        await TryAcquireAsync("a", Now, Now.AddHours(4));

        Assert.True(await TryAcquireAsync("b", Now.AddHours(4), Now.AddHours(8)));
        Assert.Equal(("b", Now.AddHours(8)), await LeaseAsync());
    }

    [Fact]
    public async Task TryAcquire_TheHolderCanRenewAfterExpiry_ButNotBefore()
    {
        await TryAcquireAsync("a", Now, Now.AddHours(4));

        Assert.False(await TryAcquireAsync("a", Now.AddHours(3), Now.AddHours(7)));
        Assert.True(await TryAcquireAsync("a", Now.AddHours(4), Now.AddHours(8)));
    }

    [Fact]
    public async Task Acquire_TakesTheLeaseFromACurrentHolder()
    {
        await TryAcquireAsync("a", Now, Now.AddHours(4));

        await using (var context = CreateContext())
            await new SchedulerLockRepository(context).AcquireAsync(Key, "b", Now.AddHours(1), Now.AddHours(5), CancellationToken.None);

        Assert.Equal(("b", Now.AddHours(5)), await LeaseAsync());
    }

    [Fact]
    public async Task Acquire_WithoutARow_CreatesIt()
    {
        await using (var context = CreateContext())
            await new SchedulerLockRepository(context).AcquireAsync(Key, "b", Now, Now.AddHours(4), CancellationToken.None);

        Assert.Equal(("b", Now.AddHours(4)), await LeaseAsync());
    }

    [Fact]
    public async Task TryInsert_WhenAnotherReplicaInsertedTheRowFirst_ReturnsFalse()
    {
        // The race's losing side: both replicas saw no row, the other one inserted first, and this insert
        // hits the primary key.
        await TryAcquireAsync("a", Now, Now.AddHours(4));
        await using var context = CreateContext();

        Assert.False(await new SchedulerLockRepository(context).TryInsertAsync(Key, "b", Now, Now.AddHours(4), CancellationToken.None));
        Assert.Equal("a", (await LeaseAsync()).Holder);
    }

    [Fact]
    public async Task TryAcquire_WhenItsOwnUpdateAlreadyCommitted_Succeeds()
    {
        // A retried attempt after an ambiguous commit: the lease is already exactly what this call writes,
        // so the UPDATE matches nothing, but the lease is ours.
        await TryAcquireAsync("a", Now, Now.AddHours(4));

        Assert.True(await TryAcquireAsync("a", Now, Now.AddHours(4)));
        Assert.False(await TryAcquireAsync("b", Now, Now.AddHours(4)));
    }

    [Fact]
    public async Task TryInsert_WhenItsOwnInsertAlreadyCommitted_Succeeds()
    {
        await TryAcquireAsync("a", Now, Now.AddHours(4));
        await using var context = CreateContext();

        Assert.True(await new SchedulerLockRepository(context).TryInsertAsync(Key, "a", Now, Now.AddHours(4), CancellationToken.None));
    }

    [Fact]
    public async Task TryInsert_WhenTheInsertFailsForAnotherReason_Throws()
    {
        // Not a key conflict (no row exists): a failure must fail the tick, not pass for lost contention.
        await using var context = database.CreateContext([new FailingSaves()]);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => new SchedulerLockRepository(context).TryInsertAsync(Key, "b", Now, Now.AddHours(4), CancellationToken.None));
    }

    private sealed class FailingSaves : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
            => throw new DbUpdateException("Simulated failure (not a key conflict).");
    }
}
