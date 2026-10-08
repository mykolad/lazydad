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
    // Static files with an unknown extension aren't served, so check the unusual ones too.
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("site.webmanifest", "application/manifest+json")]
    [InlineData("logo.svg", "image/svg+xml")]
    public async Task PageAssets_AreServed(string path, string mediaType)
    {
        // The page is a shell (app.js/app.css render it) with brand files from wwwroot.
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
    public async Task JokePage_HasTheJokeInItsPreview_AndSimilarJokes()
    {
        var feed = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes/feed?sort=new&limit=1"), SmokeTarget.ColdStartTimeout, "/jokes/feed");
        var id = feed.GetProperty("items")[0].GetProperty("id").GetInt32();

        // The shared link: the shell, with the joke's own address in its link-preview tags.
        using var page = await target.Client.GetAsync($"j/{id}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Matches($"<meta property=\"og:url\" content=\"https?://[^\"]+/j/{id}\">", html);
        Assert.Contains("<meta property=\"og:title\"", html);
        Assert.Contains("src=\"/app.js?v=", html);

        var similar = await target.GetJsonAsync($"jokes/{id}/similar?limit=4");
        var ids = similar.EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToList();
        Assert.InRange(ids.Count, 0, 4);
        Assert.DoesNotContain(id, ids);
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
    public async Task Me_WhenSignedOut_SaysSo_AndIsNeverCached()
    {
        // Polls /me itself: while the old revision still answers some requests, it has no /me (404), so a revision
        // check on another endpoint first wouldn't make this request land on the new one.
        var (cacheControl, me) = await target.PollAsync<(string CacheControl, JsonElement Me)>(async () =>
        {
            using var response = await target.Client.GetAsync("me");
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            return (response.Headers.CacheControl?.ToString() ?? "", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }, SmokeTarget.ColdStartTimeout, "/me to answer");

        Assert.Contains("no-store", cacheControl);
        Assert.False(me.GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task DeleteMyVotes_WhenSignedOut_IsUnauthorized()
    {
        // Polled for the same reason as /me: the draining revision has no such endpoint (404 or 405).
        var status = await target.PollAsync<HttpStatusCode>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "me/votes");
            request.Headers.Add("X-LazyDad", "1");
            using var response = await target.Client.SendAsync(request);
            return response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed ? null : response.StatusCode;
        }, SmokeTarget.ColdStartTimeout, "DELETE /me/votes to answer");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task ThisRevision_SignInKeyRingWorks()
    {
        // The new revision checks its key ring (database + Key Vault key) in the background at startup. Every environment
        // Deploy Environment deploys has sign-in configured: "off" means its pepper setting is gone, "failed" that the key
        // ring can't be used (a missing Key Vault role or a wrong DataProtection:KeyVaultKeyId).
        var keyRing = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("status");
            return target.IsFromTheProcessUnderTest(json) && json.TryGetProperty("signIn", out var signIn)
                && signIn.GetProperty("keyRing").GetString() != "pending" ? signIn.GetProperty("keyRing").Clone() : null;
        }, SmokeTarget.ColdStartTimeout, $"revision '{target.ExpectedRevision}' to finish its key ring check" +
            (target.NotProcess is null ? "" : $", from a process other than {target.NotProcess}"));

        Assert.Equal("ok", keyRing.GetString());
    }

    /// <summary>Where each provider's sign-in page is; a provider enabled without one here fails the test until it's added.</summary>
    private static readonly IReadOnlyDictionary<string, string> AuthorizeUrls = new Dictionary<string, string>
    {
        ["github"] = "https://github.com/login/oauth/authorize",
        ["google"] = "https://accounts.google.com/o/oauth2/v2/auth",
        ["microsoft"] = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
    };

    [Fact]
    public async Task EveryEnabledSignInProvider_AcceptsTheAppsClient_AndGetsReadersWithTheRightCallback()
    {
        // The new revision probes each configured provider at startup (a made-up code exchanged with the app's client id
        // and secret). "invalid" fails the deploy: a wrong id, or a secret that expired or was replaced, is a setting
        // that went wrong. "unreachable" (the provider didn't answer) doesn't: a provider's outage isn't this revision's,
        // rolling back wouldn't help, and LazyDadSignInProviderDown alerts if it lasts. "off": not configured here.
        var providers = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("status");
            return target.IsFromTheProcessUnderTest(json) && json.TryGetProperty("signIn", out var signIn) && signIn.TryGetProperty("providers", out var states)
                && states.EnumerateObject().All(p => p.Value.GetString() != "pending") ? states.Clone() : null;
        }, SmokeTarget.ColdStartTimeout, $"revision '{target.ExpectedRevision}' to probe its sign-in providers" +
            (target.NotProcess is null ? "" : $", from a process other than {target.NotProcess}"));

        using var noRedirects = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = target.Client.BaseAddress };
        foreach (var provider in providers.EnumerateObject().Where(p => p.Value.GetString() != "off"))
        {
            Assert.True(provider.Value.GetString() is "valid" or "unreachable",
                $"{provider.Name} is {provider.Value.GetString()} on /status: it refuses the app's client id or secret.");
            // An OpenID Connect provider's redirect needs its discovery document first, so it fails while the provider is
            // down; that's still the provider's outage, not the deploy's.
            if (provider.Value.GetString() == "unreachable")
                continue;

            // The start of a sign-in (it counts as "started" in lazydad_signins_total, once per deploy).
            using var response = await noRedirects.GetAsync($"auth/signin/{provider.Name}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location!;
            Assert.True(AuthorizeUrls.TryGetValue(provider.Name, out var authorize), $"Add {provider.Name}'s authorize URL to the smoke test.");
            Assert.Equal(authorize, location.GetLeftPart(UriPartial.Path));
            var query = System.Web.HttpUtility.ParseQueryString(location.Query);
            Assert.False(string.IsNullOrEmpty(query["client_id"]));
            // The callback the provider must have registered: this app's own address (https, from the ingress' forwarded
            // headers). Production's registration has lazydad.fyi's, where readers come from.
            Assert.Equal(new Uri(target.Client.BaseAddress!, $"signin-{provider.Name}").AbsoluteUri, query["redirect_uri"]);
        }
    }

    [Fact]
    public async Task ThePage_OffersSignInWithExactlyTheEnabledProviders()
    {
        // The header's sign-in dialog lists the providers from the shell's config: one that's off would send readers to a
        // 404, and a missing one would hide a working provider.
        var config = await target.PollAsync<JsonElement>(async () =>
        {
            using var response = await target.Client.GetAsync("");
            var html = await response.Content.ReadAsStringAsync();
            if (target.ExpectedVersion is not null && !html.Contains($"<span class=\"ld-sha\">{target.ExpectedVersion}</span>"))
                return null;
            var json = System.Text.RegularExpressions.Regex.Match(html, "<script type=\"application/json\" id=\"ld-config\">(.*?)</script>");
            return json.Success ? JsonDocument.Parse(json.Groups[1].Value).RootElement.Clone() : null;
        }, SmokeTarget.ColdStartTimeout, $"the home page to show version '{target.ExpectedVersion}'");
        var providers = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("status");
            return target.IsFromTheProcessUnderTest(json) ? json.GetProperty("signIn").GetProperty("providers").Clone() : null;
        }, SmokeTarget.ColdStartTimeout, $"revision '{target.ExpectedRevision}' to answer /status");

        var enabled = providers.EnumerateObject().Where(p => p.Value.GetString() != "off").Select(p => p.Name).Order();
        var offered = config.GetProperty("signIn").EnumerateArray().Select(p => p.GetString()!).Order();
        Assert.Equal(enabled, offered);
    }

    [Fact]
    public async Task PrivacyPage_IsServed_InBothLanguages()
    {
        // The providers' registrations link to it, and their crawlers read it without running app.js.
        var html = await target.PollAsync<(bool Found, string Html)>(async () =>
        {
            using var response = await target.Client.GetAsync("privacy");
            return response.StatusCode == HttpStatusCode.OK ? (true, await response.Content.ReadAsStringAsync()) : null;
        }, SmokeTarget.ColdStartTimeout, "/privacy");

        Assert.Contains("<div class=\"ld-doc-body\" lang=\"uk\">", html.Html);
        Assert.Contains("<div class=\"ld-doc-body\" lang=\"en\">", html.Html);
    }

    [Fact]
    public async Task JokesApi_ReturnsAJsonArray()
    {
        var jokes = await target.PollAsync<JsonElement>(async () => await target.GetJsonAsync("jokes"), SmokeTarget.ColdStartTimeout, "/jokes");

        Assert.Equal(JsonValueKind.Array, jokes.ValueKind);
    }

    [Fact]
    public async Task ThisRevision_SavesAJokeInEveryLanguage()
    {
        // The new revision generates on startup and lists each joke on /status as soon as it's saved (in-memory, so
        // only the revision answering can have saved it). DB rows alone can't prove that: a draining revision's
        // periodic tick could write them. One joke per language shows that this revision reaches Azure OpenAI and
        // the database. The slowest model and the judge aren't waited for: a thinking model can take minutes, and a
        // model failing or answering late is for monitoring (Grafana alerts), not a reason to fail a deploy.
        var configured = SmokeTarget.ConfiguredLanguages();
        Assert.NotEmpty(configured);

        // savedJokes is in memory, so each joke on it was saved by the process answering. After a restart
        // (SMOKE_NOT_PROCESS), only the restarted process counts.
        var status = await target.PollAsync<JsonElement>(async () =>
        {
            var json = await target.GetJsonAsync("status");
            var isExpectedRevision = target.ExpectedRevision is null || json.GetProperty("revision").GetString() == target.ExpectedRevision;
            var isNewProcess = target.NotProcess is null || json.GetProperty("process").GetString() != target.NotProcess;
            var languages = json.GetProperty("savedJokes").EnumerateArray()
                .Select(j => j.GetProperty("language").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return isExpectedRevision && isNewProcess && configured.Keys.All(languages.Contains) ? json : null;
        }, SmokeTarget.GenerationTimeout, $"revision '{target.ExpectedRevision}' to save a joke in: {string.Join(", ", configured.Keys)}" +
            (target.NotProcess is null ? "" : $", from a process other than {target.NotProcess}"));

        // The id the deploy's retry relies on to tell a restarted process from the old one.
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("process").GetString()), "/status reports no process id.");

        var jokes = await target.GetJsonAsync("jokes");
        var persisted = jokes.EnumerateArray().ToDictionary(j => j.GetProperty("id").GetInt32(), j => j.GetProperty("model").GetString());
        foreach (var joke in status.GetProperty("savedJokes").EnumerateArray())
        {
            var language = joke.GetProperty("language").GetString()!;
            var model = joke.GetProperty("model").GetString();
            Assert.Contains(model, configured[language]);
            // What the revision says it saved is really in the DB and served by the API.
            Assert.Equal(model, persisted.GetValueOrDefault(joke.GetProperty("id").GetInt32()));
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
