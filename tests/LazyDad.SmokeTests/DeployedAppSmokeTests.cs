using System.Net;
using System.Text.Json;

namespace LazyDad.SmokeTests;

/// <summary>
/// Runs against a deployed environment (staging before promotion, prod after it). Not part of
/// the unit test run: CI excludes Category=Smoke. The fixture is shared so the cold start is paid once.
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
    public async Task HomePage_IsServed()
    {
        var served = await target.PollAsync<bool>(async () =>
        {
            using var response = await target.Client.GetAsync("");
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            return (await response.Content.ReadAsStringAsync()).Contains("<title>LazyDad</title>");
        }, SmokeTarget.ColdStartTimeout, "the home page");

        Assert.True(served, "The home page was served but is not the LazyDad page.");
    }

    [Fact]
    public async Task JokesApi_ReturnsAJsonArray()
    {
        var jokes = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes"), SmokeTarget.ColdStartTimeout, "/jokes");

        Assert.Equal(JsonValueKind.Array, jokes.ValueKind);
    }

    [Fact]
    public async Task EveryConfiguredModel_GeneratesAFreshJoke()
    {
        // The new revision generates on startup. A fresh joke per model proves the Azure OpenAI
        // deployments, keys and DB writes all work in this environment.
        Assert.NotNull(target.DeployedAfter);
        var expected = SmokeTarget.ConfiguredModels();
        Assert.NotEmpty(expected);

        var allFresh = await target.PollAsync<bool>(async () =>
        {
            var jokes = await target.GetJsonAsync("jokes");
            var freshModels = jokes.EnumerateArray()
                .Where(j => DateTime.SpecifyKind(j.GetProperty("generatedAt").GetDateTime(), DateTimeKind.Utc) >= target.DeployedAfter)
                .Select(j => j.GetProperty("model").GetString())
                .ToHashSet();
            return expected.All(freshModels.Contains) ? true : null;
        }, SmokeTarget.GenerationTimeout, $"a joke generated after {target.DeployedAfter:O} from each of: {string.Join(", ", expected)}");

        Assert.True(allFresh);
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
