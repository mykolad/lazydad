using LazyDad.Api.Services;
using Microsoft.Extensions.Configuration;

namespace LazyDad.Tests;

public class JokeReadCacheTests
{
    private static JokeReadCache Create(string? seconds)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [JokeReadCache.SecondsKey] = seconds })
            .Build());

    [Fact]
    public async Task WithinItsLifetime_AValueIsLoadedOnce()
    {
        using var cache = Create(null);
        var loads = 0;

        var first = await cache.GetOrLoadAsync("count", () => Task.FromResult(++loads));
        var second = await cache.GetOrLoadAsync("count", () => Task.FromResult(++loads));

        Assert.Equal((1, 1), (first, second));
    }

    [Fact]
    public async Task Invalidate_MakesTheNextReadLoadAgain()
    {
        using var cache = Create(null);
        await cache.GetOrLoadAsync("count", () => Task.FromResult(1));

        cache.Invalidate();

        Assert.Equal(2, await cache.GetOrLoadAsync("count", () => Task.FromResult(2)));
    }

    [Fact]
    public async Task ZeroSeconds_TurnsItOff()
    {
        using var cache = Create("0");
        var loads = 0;

        await cache.GetOrLoadAsync("count", () => Task.FromResult(++loads));
        await cache.GetOrLoadAsync("count", () => Task.FromResult(++loads));

        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task AFailedLoad_IsNotCached()
    {
        using var cache = Create(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrLoadAsync<int>("count", () => throw new InvalidOperationException("database is down")));

        Assert.Equal(3, await cache.GetOrLoadAsync("count", () => Task.FromResult(3)));
    }

    [Fact]
    public void ANegativeLifetime_IsRefusedAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Create("-1"));

        Assert.Contains(JokeReadCache.SecondsKey, error.Message);
    }
}
