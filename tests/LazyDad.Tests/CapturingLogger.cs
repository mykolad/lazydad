using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LazyDad.Tests;

/// <summary>
/// Records log entries so tests can wait on a deterministic signal (e.g. "tick completed")
/// instead of sleeping and hoping the background work has finished.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> entries = new();

    public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => entries.Enqueue((logLevel, formatter(state, exception)));

    public async Task WaitForAsync(LogLevel level, string messageFragment, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!entries.Any(e => e.Level == level && e.Message.Contains(messageFragment, StringComparison.Ordinal)))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No {level} log containing \"{messageFragment}\" within {timeout}.");
            await Task.Delay(10);
        }
    }
}
