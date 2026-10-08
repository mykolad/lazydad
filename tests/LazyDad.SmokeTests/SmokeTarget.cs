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
/// <item><c>SMOKE_NOT_PROCESS</c>: a process id /status must not report. The retry sets it to the process that answered
/// just before it restarted the revision, which keeps its name, so the old process can't satisfy the retried checks.</item>
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
        NotProcess = NullIfBlank(Environment.GetEnvironmentVariable("SMOKE_NOT_PROCESS"));
        AccessToken = NullIfBlank(Environment.GetEnvironmentVariable("SMOKE_ACCESS_TOKEN"));
    }

    /// <summary>
    /// <c>SMOKE_ACCESS_TOKEN</c>: the deploy identity's Entra token for <c>api://lazydad-smoke</c>, which the app accepts
    /// as one fixed voter where a signed-in reader's votes are (SmokeSignIn). Deploy Environment gets it with
    /// <c>az account get-access-token</c> where the environment's <c>SMOKE_TOKEN_RESOURCE</c> variable is set.
    /// </summary>
    public string? AccessToken { get; }

    public HttpClient Client { get; }
    public string? ExpectedVersion { get; }
    public string? ExpectedRevision { get; }
    public string? NotProcess { get; }

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

    /// <summary>
    /// Whether a <c>/status</c> answer came from the process under test: the expected revision, and on the retry not the
    /// process that answered before the restart (the revision keeps its name, and the old process may still answer).
    /// </summary>
    public bool IsFromTheProcessUnderTest(JsonElement status)
        => (ExpectedRevision is null || status.GetProperty("revision").GetString() == ExpectedRevision)
            && (NotProcess is null || status.GetProperty("process").GetString() != NotProcess);

    /// <summary>
    /// Whether an answer came from the process under test, by the headers every answer carries
    /// (<c>X-LazyDad-Revision</c>, <c>X-LazyDad-Process</c>): while the old revision drains it answers some requests, and
    /// once it has the same endpoint, only these tell the two apart. An answer without them is an older build's.
    /// </summary>
    public bool IsFromTheProcessUnderTest(HttpResponseMessage response)
    {
        static string? Header(HttpResponseMessage response, string name)
            => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        return (ExpectedRevision is null || Header(response, "X-LazyDad-Revision") == ExpectedRevision)
            && (NotProcess is null || Header(response, "X-LazyDad-Process") is { } process && process != NotProcess);
    }

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
