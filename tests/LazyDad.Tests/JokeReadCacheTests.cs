using LazyDad.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LazyDad.Tests;

public class JokeReadCacheTests
{
    private static JokeReadCache Create(string? seconds)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [JokeReadCache.SecondsKey] = seconds })
            .Build(), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task WithinItsLifetime_AValueIsLoadedOnce()
    {
        using var cache = Create(null);
        var loads = 0;

        var first = await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(++loads), CancellationToken.None);
        var second = await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(++loads), CancellationToken.None);

        Assert.Equal((1, 1), (first, second));
    }

    [Fact]
    public async Task VisitorsMissingTheCacheTogether_ShareOneLoad()
    {
        using var cache = Create(null);
        var loads = 0;
        var database = new TaskCompletionSource<int>();

        // Ten requests arrive while the first database query is still running.
        var reads = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrLoadAsync("count", (_, _) => { Interlocked.Increment(ref loads); return database.Task; }, CancellationToken.None))
            .ToList();
        database.SetResult(955);

        Assert.All(await Task.WhenAll(reads), count => Assert.Equal(955, count));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task ACallerGivingUp_DoesntCancelTheLoadOthersWaitFor()
    {
        using var cache = Create(null);
        var database = new TaskCompletionSource<int>();
        using var leaving = new CancellationTokenSource();
        CancellationToken loadToken = default;

        var gaveUp = cache.GetOrLoadAsync("count", (_, token) => { loadToken = token; return database.Task; }, leaving.Token);
        var waiting = cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(-1), CancellationToken.None);
        await leaving.CancelAsync();
        database.SetResult(955);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.Equal(955, await waiting);
        Assert.False(loadToken.CanBeCanceled);
    }

    [Fact]
    public async Task ALoad_UsesItsOwnScope_WhichLastsUntilTheQueryEnds_EvenIfItsCallerGivesUp()
    {
        await using var services = new ServiceCollection().AddScoped<Connection>().BuildServiceProvider();
        using var cache = new JokeReadCache(new ConfigurationBuilder().Build(), services.GetRequiredService<IServiceScopeFactory>());
        var database = new TaskCompletionSource<int>();
        using var leaving = new CancellationTokenSource();
        Connection? used = null;

        var gaveUp = cache.GetOrLoadAsync("count", (loadServices, _) => { used = loadServices.GetRequiredService<Connection>(); return database.Task; }, leaving.Token);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);

        // The caller has gone, but the query is still running: its connection must still be open.
        Assert.False(used!.Disposed);
        database.SetResult(955);
        Assert.Equal(955, await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(-1), CancellationToken.None));
        Assert.True(used.Disposed);
    }

    // Stands in for a scoped DbContext.
    private sealed class Connection : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task EntriesCostTheirRows_SoAPageBeyondTheBudgetIsntKept()
    {
        using var cache = Create(null);
        var loads = 0;
        Func<IServiceProvider, CancellationToken, Task<int[]>> load = (_, _) => { loads++; return Task.FromResult(new int[JokeReadCache.MaxRows + 1]); };

        await cache.GetOrLoadAsync("huge", load, CancellationToken.None);
        await cache.GetOrLoadAsync("huge", load, CancellationToken.None);

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Invalidate_MakesTheNextReadLoadAgain()
    {
        using var cache = Create(null);
        await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(1), CancellationToken.None);

        cache.Invalidate();

        Assert.Equal(2, await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task ZeroSeconds_TurnsItOff()
    {
        using var cache = Create("0");
        var loads = 0;

        await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(++loads), CancellationToken.None);
        await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(++loads), CancellationToken.None);

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task AFailedLoad_IsNotCached()
    {
        using var cache = Create(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrLoadAsync<int>("count", (_, _) => throw new InvalidOperationException("database is down"), CancellationToken.None));

        Assert.Equal(3, await cache.GetOrLoadAsync("count", (_, _) => Task.FromResult(3), CancellationToken.None));
    }

    [Fact]
    public void ANegativeLifetime_IsRefusedAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Create("-1"));

        Assert.Contains(JokeReadCache.SecondsKey, error.Message);
    }
}
