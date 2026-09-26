using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LazyDad.Tests;

/// <summary>Runs against a real (SQLite in-memory) database, so the atomic UPDATE and the key conflict are real.</summary>
public sealed class SchedulerLockRepositoryTests : IDisposable
{
    private const string Key = "jokes:Ukrainian";
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public SchedulerLockRepositoryTests()
    {
        connection.Open();
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private LazyDadDbContext CreateContext()
        => new(new DbContextOptionsBuilder<LazyDadDbContext>().UseSqlite(connection).Options);

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
    public async Task TryAcquire_WhenAnotherReplicaInsertedFirst_Fails()
    {
        // Both replicas saw no row; the second insert hits the primary key.
        await using var first = CreateContext();
        await using var second = CreateContext();
        Assert.True(await new SchedulerLockRepository(first).TryAcquireAsync(Key, "a", Now, Now.AddHours(4), CancellationToken.None));

        Assert.False(await new SchedulerLockRepository(second).TryAcquireAsync(Key, "b", Now, Now.AddHours(4), CancellationToken.None));
    }
}
