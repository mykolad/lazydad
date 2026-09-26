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
    public async Task HomePage_IsServed_WithTheDeployedVersion()
    {
        // The new revision writes the page on startup, so wait until it shows the deployed version.
        var html = await target.PollAsync<(bool Found, string Html)>(async () =>
        {
            using var response = await target.Client.GetAsync("");
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            var body = await response.Content.ReadAsStringAsync();
            // Keep polling until the version link exists: the expected version when one is given, else any.
            var ready = target.ExpectedVersion is null
                ? body.Contains("class=\"ld-version\"")
                : body.Contains($"<span class=\"ld-sha\">{target.ExpectedVersion}</span>");
            return ready ? (true, body) : null;
        }, SmokeTarget.ColdStartTimeout, $"the home page to show version '{target.ExpectedVersion}'");

        Assert.Contains("<title>LazyDad</title>", html.Html);
        Assert.Matches(@"<span>v(\d{4}\.\d{2}\.\d{2}|\S+ \(local build\))</span>", html.Html);
    }

    [Theory]
    [InlineData("app.js", "text/javascript")]
    [InlineData("app.css", "text/css")]
    public async Task PageAssets_AreServed(string path, string mediaType)
    {
        // The page is a shell: without these, it shows nothing but the loading skeleton.
        using var response = await target.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task FeedAndSummary_ServeThePagesData()
    {
        var feed = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes/feed?sort=top&limit=5"), SmokeTarget.ColdStartTimeout, "/jokes/feed");
        var summary = await target.GetJsonAsync("jokes/summary");

        Assert.True(feed.GetProperty("total").GetInt32() > 0, "The feed reports no jokes.");
        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.InRange(items.Count, 1, 5);
        // "top" is by net score, highest first.
        var scores = items.Select(j => j.GetProperty("up").GetInt32() - j.GetProperty("down").GetInt32()).ToList();
        Assert.Equal(scores.OrderDescending(), scores);
        Assert.True(summary.GetProperty("count").GetInt32() > 0, "The summary reports no jokes.");
        Assert.True(summary.TryGetProperty("nextBatchAt", out _), "The summary has no nextBatchAt.");
    }

    [Fact]
    public async Task Vote_WithoutAChange_ReturnsTheCounts()
    {
        // value = previous = 0 changes nothing, so this checks the endpoint (routing, rate limiter,
        // DB read) without casting a vote in the environment.
        var feed = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes/feed?sort=new&limit=1"), SmokeTarget.ColdStartTimeout, "/jokes/feed");
        var joke = feed.GetProperty("items")[0];

        using var response = await target.Client.PostAsync(
            $"jokes/{joke.GetProperty("id").GetInt32()}/vote",
            new StringContent("{\"value\":0,\"previous\":0}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var counts = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(counts.RootElement.GetProperty("up").GetInt32() >= 0);
        Assert.True(counts.RootElement.GetProperty("down").GetInt32() >= 0);
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
