using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace LazyDad.Api.Services;

/// <summary>
/// Keeps what every visitor reads the same (the joke count, the Top 3, the feed's pages) in memory for
/// <see cref="SecondsKey"/> seconds (<see cref="DefaultSeconds"/> unless set; 0 turns it off), so the database
/// serves them once per replica per period, not once per visitor: the load test (runbook section 12) found the
/// database to be the first limit. A vote isn't seen by other visitors until their copy expires (the voter's own counts
/// come from the vote's response). The replica that saves a joke or updates the Top 3 drops its copies at once
/// (<see cref="Invalidate"/>); the other region's replica sees them when its copies expire.
/// </summary>
public sealed class JokeReadCache : IDisposable
{
    public const string SecondsKey = "ReadCache:Seconds";
    public const int DefaultSeconds = 30;
    // Bounds the entries (one per key): the feed has a key per page, sort and size, and anyone can ask for any of them.
    private const int MaxEntries = 2000;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    // The load in progress for each key: visitors who miss the cache at the same moment (when an entry expires, or right
    // after Invalidate) wait for one database query instead of each running their own.
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> loading = new();
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeSpan lifetime;
    // Part of every key: Invalidate moves to a new generation, so the old entries are never read again (they expire).
    private long generation;

    public JokeReadCache(IConfiguration configuration, IServiceScopeFactory scopeFactory)
    {
        var seconds = configuration.GetValue<int?>(SecondsKey) ?? DefaultSeconds;
        if (seconds < 0)
            throw new InvalidOperationException($"{SecondsKey} must be 0 (off) or more (was {seconds}).");
        lifetime = TimeSpan.FromSeconds(seconds);
        this.scopeFactory = scopeFactory;
    }

    /// <summary>
    /// The cached value for <paramref name="key"/>, or what <paramref name="load"/> returns (then cached). The load gets
    /// the services of its own DI scope (resolve repositories from them, not from the caller's request). It's shared by
    /// everyone waiting for the key, so it runs without any one caller's cancellation (a short query either way);
    /// <paramref name="cancellationToken"/> only stops this caller's wait. A failed load isn't cached.
    /// </summary>
    public async Task<T> GetOrLoadAsync<T>(string key, Func<IServiceProvider, CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        if (lifetime == TimeSpan.Zero)
            return await LoadInOwnScopeAsync(load, cancellationToken);

        var fullKey = $"{Interlocked.Read(ref generation)}:{key}";
        if (cache.TryGetValue(fullKey, out T? cached))
            return cached!;

        var pending = new Lazy<Task<object?>>(() => LoadAsync(fullKey, load));
        var shared = loading.GetOrAdd(fullKey, pending);
        return (T)(await shared.Value.WaitAsync(cancellationToken))!;
    }

    private async Task<object?> LoadAsync<T>(string fullKey, Func<IServiceProvider, CancellationToken, Task<T>> load)
    {
        try
        {
            var value = await LoadInOwnScopeAsync(load, CancellationToken.None);
            cache.Set(fullKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = 1 });
            return value;
        }
        finally
        {
            // Cached now (or failed, so the next caller tries again): later callers read the entry, not this load.
            loading.TryRemove(fullKey, out _);
        }
    }

    // Every load gets its own DI scope, so its DbContext lives exactly as long as the query: a shared load mustn't use the
    // scope of the request that started it, which ASP.NET disposes when that request ends or is cancelled.
    private async Task<T> LoadInOwnScopeAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await load(scope.ServiceProvider, cancellationToken);
    }

    /// <summary>Drops every cached value: the next reads load from the database.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
        cache.Compact(1.0);
    }

    public void Dispose() => cache.Dispose();
}
