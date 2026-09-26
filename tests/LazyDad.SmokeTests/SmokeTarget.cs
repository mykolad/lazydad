using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace LazyDad.SmokeTests;

/// <summary>
/// The deployed app under test, configured by the Deploy Master pipeline:
/// <list type="bullet">
/// <item><c>SMOKE_BASE_URL</c>: the app's https URL (required).</item>
/// <item><c>SMOKE_EXPECTED_VERSION</c>: the commit the new revision must report on /healthz.</item>
/// <item><c>SMOKE_EXPECTED_REVISION</c>: the Container Apps revision /healthz and /status must report; unique
/// per rollout, so neither a re-deploy of the same commit nor the draining revision can satisfy the checks.</item>
/// <item><c>SMOKE_TICKS_AFTER</c>: an ISO 8601 time; /status ticks must have completed after it. The retry sets it just
/// before restarting the revision, which keeps its name, so the old process's ticks can't satisfy the retried checks.</item>
/// </list>
/// Missing SMOKE_BASE_URL fails loudly: a smoke run that silently tests nothing is worse than none.
/// </summary>
public sealed class SmokeTarget : IDisposable
{
    public static readonly TimeSpan ColdStartTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(4);

    public SmokeTarget()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SMOKE_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("SMOKE_BASE_URL is not set; smoke tests need a deployed app to target.");

        Client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        ExpectedVersion = NullIfBlank(Environment.GetEnvironmentVariable("SMOKE_EXPECTED_VERSION"));
        ExpectedRevision = NullIfBlank(Environment.GetEnvironmentVariable("SMOKE_EXPECTED_REVISION"));
        var ticksAfter = NullIfBlank(Environment.GetEnvironmentVariable("SMOKE_TICKS_AFTER"));
        TicksAfter = ticksAfter is null ? null : DateTimeOffset.Parse(ticksAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public HttpClient Client { get; }
    public string? ExpectedVersion { get; }
    public string? ExpectedRevision { get; }
    public DateTimeOffset? TicksAfter { get; }

    /// <summary>
    /// Polls until <paramref name="check"/> returns a value, tolerating transient failures
    /// (scale-from-zero cold start, the old revision still serving during a swap).
    /// </summary>
    public async Task<T> PollAsync<T>(Func<Task<T?>> check, TimeSpan timeout, string what) where T : struct
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await check();
                if (result.HasValue)
                    return result.Value;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        throw new TimeoutException($"Timed out after {timeout} waiting for {what}.", last);
    }

    public Task<JsonElement> GetJsonAsync(string path) => Client.GetFromJsonAsync<JsonElement>(path);

    /// <summary>Enabled languages and their models, from the deployed appsettings.json.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ConfiguredLanguages()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deployed-appsettings.json")));
        return document.RootElement.GetProperty("JokeGeneration").GetProperty("Languages").EnumerateArray()
            .Where(l => l.GetProperty("Enabled").GetBoolean())
            .ToDictionary(
                l => l.GetProperty("Language").GetString()!,
                l => (IReadOnlyList<string>)l.GetProperty("LlmModels").EnumerateArray().Select(m => m.GetProperty("Model").GetString()!).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() => Client.Dispose();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
