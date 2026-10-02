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
    // The memory budget, in rows: each entry costs the rows it holds (a feed page up to 51 jokes, the count 1), since
    // anyone can ask for any page. A joke's text is at most 2,000 characters (4 KB), so 20,000 rows stay under about 80 MB
    // of the replica's 512 MB even then; real jokes (about 150 characters) take about a tenth. Past it, MemoryCache
    // doesn't keep the new entry and makes room by dropping others (expired ones first).
    public const int MaxRows = 20_000;

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = MaxRows });
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
            cache.Set(fullKey, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = Rows(value) });
            return value;
        }
        finally
        {
            // Cached now (or failed, so the next caller tries again): later callers read the entry, not this load.
            loading.TryRemove(fullKey, out _);
        }
    }

    private static long Rows<T>(T value) => value is System.Collections.ICollection rows ? Math.Max(1, rows.Count) : 1;

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
