using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LazyDad.Api.Networking;

namespace LazyDad.Tests;

/// <summary>The app over HTTP (see <see cref="LazyDadApp"/>): what a request goes through before a controller, and after.</summary>
public sealed class AppHttpTests : IDisposable
{
    private const int VotesPerMinute = 3;

    private readonly LazyDadApp app = new(new() { [VoteRateLimit.ConfigurationKey] = VotesPerMinute.ToString() });
    private readonly HttpClient client;

    public AppHttpTests()
    {
        client = app.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        app.Dispose();
    }

    private static HttpRequestMessage Vote(string path, int value, string clientAddress) => new(HttpMethod.Post, path)
    {
        Content = JsonContent.Create(new { value, previous = 0 }),
        // What the Container Apps ingress adds: the address the vote limit counts by.
        Headers = { { "X-Forwarded-For", clientAddress } },
    };

    [Theory]
    [InlineData("jokes/feed?sort=best&limit=20")]
    [InlineData("jokes/feed?sort=new&limit=0")]
    [InlineData("jokes/feed?sort=new&limit=51")]
    [InlineData("jokes/feed?sort=new&limit=20&after=not-a-cursor")]
    [InlineData("jokes/feed?sort=new&limit=lots")]
    [InlineData("jokes/1/similar?limit=13")]
    public async Task BadQueries_AreRejected(string path)
    {
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheFeed_ServesThePageAndItsCursor()
    {
        await app.AddJokeAsync("Older");
        await app.AddJokeAsync("Newer");

        using var first = JsonDocument.Parse(await client.GetStringAsync("jokes/feed?sort=new&limit=1"));
        var next = first.RootElement.GetProperty("next").GetString();
        using var second = JsonDocument.Parse(await client.GetStringAsync($"jokes/feed?sort=new&limit=1&after={Uri.EscapeDataString(next!)}"));

        Assert.Equal(2, first.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("Newer", first.RootElement.GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("Older", second.RootElement.GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task AVote_AnswersTheJokesNewCounts()
    {
        var joke = await app.AddJokeAsync("Funny");

        using var response = await client.SendAsync(Vote($"jokes/{joke.Id}/vote", 1, "203.0.113.1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"up":1,"down":0}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AVote_ForAnUnknownJoke_IsNotFound_AndANonNumericId_HasNoRoute()
    {
        using var unknown = await client.SendAsync(Vote("jokes/999/vote", 1, "203.0.113.1"));
        using var notANumber = await client.SendAsync(Vote("jokes/abc/vote", 1, "203.0.113.1"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notANumber.StatusCode);
    }

    [Fact]
    public async Task VotesPastTheLimit_FromOneAddress_AreRefused_WhileAnotherAddressStillVotes()
    {
        var joke = await app.AddJokeAsync("Popular");

        for (var i = 0; i < VotesPerMinute; i++)
        {
            using var allowed = await client.SendAsync(Vote($"jokes/{joke.Id}/vote", 0, "203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using var refused = await client.SendAsync(Vote($"jokes/{joke.Id}/vote", 0, "203.0.113.1"));
        using var other = await client.SendAsync(Vote($"jokes/{joke.Id}/vote", 0, "203.0.113.2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task AJokesPage_HasItsLinkPreview_AndAnUnknownJokesPageIsNotFound()
    {
        var joke = await app.AddJokeAsync("A joke worth sharing");

        using var page = await client.GetAsync($"j/{joke.Id}");
        using var unknown = await client.GetAsync("j/999");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>A joke worth sharing — LazyDad</title>", html);
        Assert.Contains("<meta property=\"og:title\" content=\"A joke worth sharing\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"http://localhost/j/{joke.Id}\">", html);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("<title>LazyDad</title>", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheHomePage_IsTheShellWrittenAtStartup()
    {
        using var response = await client.GetAsync("");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>LazyDad</title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthAndStatus_Answer()
    {
        using var health = JsonDocument.Parse(await client.GetStringAsync("healthz"));
        using var status = JsonDocument.Parse(await client.GetStringAsync("status"));

        Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
        Assert.Equal("local", status.RootElement.GetProperty("revision").GetString());
        Assert.Empty(status.RootElement.GetProperty("savedJokes").EnumerateArray());
        // No pepper configured, so sign-in is off.
        Assert.Equal("off", status.RootElement.GetProperty("signIn").GetProperty("keyRing").GetString());
    }
}
