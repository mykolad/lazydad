using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace LazyDad.SmokeTests;

/// <summary>
/// The deployed app under test, configured by the CD pipeline:
/// <list type="bullet">
/// <item><c>SMOKE_BASE_URL</c>: the app's https URL (required).</item>
/// <item><c>SMOKE_EXPECTED_VERSION</c>: the commit the new revision must report on /healthz.</item>
/// <item><c>SMOKE_EXPECTED_REVISION</c>: the Container Apps revision /healthz must report; unique per rollout,
/// so a re-deploy of the same commit can't be satisfied by the draining revision.</item>
/// <item><c>SMOKE_DEPLOYED_AFTER</c>: ISO-8601 UTC time; jokes generated after it prove the new revision's LLM calls work.</item>
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
        DeployedAfter = DateTime.TryParse(
            Environment.GetEnvironmentVariable("SMOKE_DEPLOYED_AFTER"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var after) ? after : null;
    }

    public HttpClient Client { get; }
    public string? ExpectedVersion { get; }
    public string? ExpectedRevision { get; }
    public DateTime? DeployedAfter { get; }

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

    /// <summary>Models configured for enabled languages in the deployed appsettings.json.</summary>
    public static IReadOnlyList<string> ConfiguredModels()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deployed-appsettings.json")));
        return document.RootElement.GetProperty("JokeGeneration").GetProperty("Languages").EnumerateArray()
            .Where(l => l.GetProperty("Enabled").GetBoolean())
            .SelectMany(l => l.GetProperty("LlmModels").EnumerateArray().Select(m => m.GetProperty("Model").GetString()!))
            .Distinct()
            .ToList();
    }

    public void Dispose() => Client.Dispose();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
