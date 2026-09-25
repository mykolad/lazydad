using System.Net;
using System.Text.Json;

namespace LazyDad.SmokeTests;

/// <summary>
/// Runs against a deployed environment (staging before promotion, prod after it), from Deploy Master.
/// Not part of the unit test run: tools/coverage.ps1 tests only tests/LazyDad.Tests. The fixture is
/// shared so the cold start is paid once.
/// </summary>
[Trait("Category", "Smoke")]
public class DeployedAppSmokeTests : IClassFixture<SmokeTarget>
{
    private readonly SmokeTarget target;

    public DeployedAppSmokeTests(SmokeTarget target)
    {
        this.target = target;
    }

    [Fact]
    public async Task Healthz_ReportsHealthyAndTheDeployedVersionAndRevision()
    {
        // Waits out the scale-from-zero cold start and the old revision draining. The revision is
        // what makes this rollout unique; the version (commit) alone repeats on a re-deploy.
        var health = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("healthz");
            // Older builds lack these fields: treat that as "not the new revision yet".
            return Matches(json, "version", target.ExpectedVersion) && Matches(json, "revision", target.ExpectedRevision) ? json : null;
        }, SmokeTarget.ColdStartTimeout, $"/healthz to report version '{target.ExpectedVersion}' and revision '{target.ExpectedRevision}'");

        Assert.Equal("healthy", health.GetProperty("status").GetString());

        static bool Matches(JsonElement json, string property, string? expected)
            => expected is null || (json.TryGetProperty(property, out var value) && value.GetString() == expected);
    }

    [Fact]
    public async Task HomePage_IsServed_WithTheDeployedVersionInTheFooter()
    {
        // The new revision regenerates the page on startup, so wait until its footer shows the version.
        var html = await target.PollAsync<(bool Found, string Html)>(async () =>
        {
            using var response = await target.Client.GetAsync("");
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            var body = await response.Content.ReadAsStringAsync();
            // Keep polling until the footer exists: the expected version when one is given, else any version.
            var ready = target.ExpectedVersion is null
                ? body.Contains("<footer>Version ")
                : body.Contains($">{target.ExpectedVersion}</a></footer>");
            return ready ? (true, body) : null;
        }, SmokeTarget.ColdStartTimeout, $"the home page footer to show version '{target.ExpectedVersion}'");

        Assert.Contains("<title>LazyDad</title>", html.Html);
        Assert.Matches(@"<footer>Version (\d{4}\.\d{2}\.\d{2}|\S+ \(local build\))", html.Html);
    }

    [Fact]
    public async Task JokesApi_ReturnsAJsonArray()
    {
        var jokes = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes"), SmokeTarget.ColdStartTimeout, "/jokes");

        Assert.Equal(JsonValueKind.Array, jokes.ValueKind);
    }

    [Fact]
    public async Task ThisRevision_SavesAJokeFromEveryConfiguredModel_AndJudgesThem()
    {
        // The new revision generates on startup and reports its own tick on /status (in-memory,
        // so only the revision answering can have produced it). DB rows alone can't prove that:
        // a draining revision's periodic tick could write them. This checks that the Azure OpenAI
        // deployments, keys, DB writes and the judge all work in this revision.
        var configured = SmokeTarget.ConfiguredLanguages();
        Assert.NotEmpty(configured);

        var status = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("status");
            var isExpectedRevision = target.ExpectedRevision is null || json.GetProperty("revision").GetString() == target.ExpectedRevision;
            var reported = json.GetProperty("ticks").EnumerateArray().Select(t => t.GetProperty("language").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return isExpectedRevision && configured.Keys.All(reported.Contains) ? json : null;
        }, SmokeTarget.GenerationTimeout, $"revision '{target.ExpectedRevision}' to report a tick for: {string.Join(", ", configured.Keys)}");

        var jokes = await target.GetJsonAsync("jokes");
        var persisted = jokes.EnumerateArray().ToDictionary(j => j.GetProperty("id").GetInt32(), j => j.GetProperty("model").GetString());

        foreach (var tick in status.GetProperty("ticks").EnumerateArray())
        {
            var language = tick.GetProperty("language").GetString()!;
            Assert.True(tick.GetProperty("succeeded").GetBoolean(), $"'{language}' tick failed: {tick.GetProperty("error").GetString()}.");
            Assert.NotEqual("failed", tick.GetProperty("leaderboard").GetString());

            var saved = tick.GetProperty("jokes").EnumerateArray()
                .Select(j => (Id: j.GetProperty("id").GetInt32(), Model: j.GetProperty("model").GetString()))
                .ToList();
            Assert.All(configured[language], model => Assert.Contains(saved, j => j.Model == model));
            // What the revision says it saved is really in the DB and served by the API.
            Assert.All(saved, j => Assert.Equal(j.Model, persisted.GetValueOrDefault(j.Id)));
        }
    }

    [Fact]
    public async Task Leaderboard_IsPopulatedWithContiguousRanks()
    {
        var top = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("jokes/top");
            return json.GetArrayLength() > 0 ? json : null;
        }, SmokeTarget.GenerationTimeout, "a non-empty /jokes/top");

        // Each language has its own 1-based leaderboard, so validate the ranks per language.
        foreach (var language in top.EnumerateArray().GroupBy(t => t.GetProperty("language").GetString()))
        {
            var ranks = language.Select(t => t.GetProperty("rank").GetInt32()).Order().ToList();
            Assert.True(Enumerable.Range(1, ranks.Count).SequenceEqual(ranks),
                $"Leaderboard for '{language.Key}' has ranks [{string.Join(", ", ranks)}]; expected 1..{ranks.Count}.");
        }
        Assert.All(top.EnumerateArray(), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("joke").GetProperty("text").GetString())));
    }
}
