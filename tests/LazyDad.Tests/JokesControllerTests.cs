using System.Text.Json;
using LazyDad.Api.Controllers;
using LazyDad.Api.Services;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace LazyDad.Tests;

public class JokesControllerTests
{
    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly Mock<ITopJokeRepository> topJokeRepositoryMock = new();

    private readonly SchedulerStatus schedulerStatus = new();

    private JokesController CreateController() => new(jokeRepositoryMock.Object, topJokeRepositoryMock.Object, schedulerStatus);

    private static JsonElement Json(IActionResult result)
        => JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value, JsonSerializerOptions.Web)).RootElement;

    private static Joke MakeJoke(int id) => new() { Id = id, Language = "Ukrainian", Model = "gpt-5.3-chat", Text = $"Joke {id}" };

    [Fact]
    public async Task GetAll_ReturnsOkWithAllJokes()
    {
        List<Joke> jokes = [MakeJoke(1), MakeJoke(2)];
        jokeRepositoryMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(jokes);

        var result = await CreateController().GetAll(CancellationToken.None);

        Assert.Same(jokes, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task GetById_WhenFound_ReturnsOk()
    {
        var joke = MakeJoke(7);
        jokeRepositoryMock.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(joke);

        var result = await CreateController().GetById(7, CancellationToken.None);

        Assert.Same(joke, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task GetById_WhenMissing_ReturnsNotFound()
    {
        jokeRepositoryMock.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Joke?)null);

        Assert.IsType<NotFoundResult>(await CreateController().GetById(99, CancellationToken.None));
    }

    [Fact]
    public async Task GetByLanguage_PassesLanguageThrough()
    {
        List<Joke> jokes = [MakeJoke(3)];
        jokeRepositoryMock.Setup(r => r.GetByLanguageAsync("Ukrainian", It.IsAny<CancellationToken>())).ReturnsAsync(jokes);

        var result = await CreateController().GetByLanguage("Ukrainian", CancellationToken.None);

        Assert.Same(jokes, Assert.IsType<OkObjectResult>(result).Value);
    }

    [Fact]
    public async Task GetTop_ProjectsLeaderboardWithJoke()
    {
        var joke = MakeJoke(5);
        var selectedAt = new DateTime(2026, 9, 24, 7, 43, 17, DateTimeKind.Utc);
        topJokeRepositoryMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TopJoke { Language = "Ukrainian", Rank = 1, JokeId = 5, Joke = joke, Reason = "Clever", JudgeModel = "gpt-6-sol", SelectedAt = selectedAt }
        ]);

        var result = await CreateController().GetTop(CancellationToken.None);

        // The projection is an anonymous type; check the JSON shape clients actually receive.
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(1, entry.GetProperty("rank").GetInt32());
        Assert.Equal("Ukrainian", entry.GetProperty("language").GetString());
        Assert.Equal("Clever", entry.GetProperty("reason").GetString());
        Assert.Equal("gpt-6-sol", entry.GetProperty("judgeModel").GetString());
        Assert.Equal(selectedAt, entry.GetProperty("selectedAt").GetDateTime());
        // The whole public shape: adding, removing or renaming a field must be a deliberate test change.
        Assert.Equal(
            ["language", "rank", "reason", "judgeModel", "selectedAt", "joke"],
            entry.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Joke 5", entry.GetProperty("joke").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("new", JokeSort.Newest)]
    [InlineData("top", JokeSort.TopVoted)]
    public async Task GetFeed_ReturnsThePage_TheTotal_AndACursorAfterItsLastJoke(string sort, JokeSort expected)
    {
        var last = new Joke { Id = 3, Text = "Joke 3", GeneratedAt = new DateTime(2026, 9, 23, 4, 0, 0, DateTimeKind.Utc), Up = 7, Down = 2 };
        List<Joke> page = [MakeJoke(4), last];
        jokeRepositoryMock.Setup(r => r.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(42);
        jokeRepositoryMock.Setup(r => r.GetPageAsync(expected, null, 2, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var json = Json(await CreateController().GetFeed(sort, null, 2, CancellationToken.None));

        Assert.Equal(42, json.GetProperty("total").GetInt32());
        Assert.Equal([4, 3], json.GetProperty("items").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()));
        Assert.Equal(new JokeCursor(5, last.GeneratedAt, 3).ToString(), json.GetProperty("next").GetString());
        Assert.Equal(["total", "items", "next"], json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task GetFeed_PassesTheCursorOn_AndEndsWithoutANextCursor()
    {
        var after = new JokeCursor(5, new DateTime(2026, 9, 23, 4, 0, 0, DateTimeKind.Utc), 3);
        jokeRepositoryMock.Setup(r => r.GetPageAsync(JokeSort.TopVoted, after, 20, It.IsAny<CancellationToken>())).ReturnsAsync([MakeJoke(1)]);

        var json = Json(await CreateController().GetFeed("top", after.ToString(), 20, CancellationToken.None));

        // Fewer jokes than the limit: that was the last page.
        Assert.Equal(JsonValueKind.Null, json.GetProperty("next").ValueKind);
    }

    [Theory]
    [InlineData("best", null, 20)]
    [InlineData("new", null, 0)]
    [InlineData("new", null, JokesController.MaxPageSize + 1)]
    [InlineData("new", "not-a-cursor", 20)]
    public async Task GetFeed_RejectsBadArguments(string sort, string? after, int limit)
    {
        Assert.IsType<BadRequestObjectResult>(await CreateController().GetFeed(sort, after, limit, CancellationToken.None));
        jokeRepositoryMock.Verify(r => r.GetPageAsync(It.IsAny<JokeSort>(), It.IsAny<JokeCursor?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSummary_ReturnsTheCountAndTheNextScheduledBatch()
    {
        var next = new DateTime(2026, 9, 26, 4, 0, 0, DateTimeKind.Utc);
        schedulerStatus.RecordNextTick("Ukrainian", next);
        jokeRepositoryMock.Setup(r => r.CountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(955);

        var json = Json(await CreateController().GetSummary(CancellationToken.None));

        Assert.Equal(955, json.GetProperty("count").GetInt32());
        Assert.Equal(next, json.GetProperty("nextBatchAt").GetDateTime());
    }

    [Fact]
    public async Task GetSummary_BeforeTheSchedulerPlansABatch_HasNoNextBatch()
    {
        var json = Json(await CreateController().GetSummary(CancellationToken.None));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("nextBatchAt").ValueKind);
    }

    [Theory]
    [InlineData(1, 0, 1, 0)]    // new up vote
    [InlineData(-1, 0, 0, 1)]   // new down vote
    [InlineData(0, 1, -1, 0)]   // up vote removed
    [InlineData(-1, 1, -1, 1)]  // switched from up to down
    [InlineData(1, -1, 1, -1)]  // switched from down to up
    public async Task Vote_AppliesTheChangeFromThePreviousVote(int value, int previous, int upDelta, int downDelta)
    {
        jokeRepositoryMock.Setup(r => r.AddVotesAsync(7, upDelta, downDelta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Joke { Id = 7, Up = 21, Down = 4 });

        var json = Json(await CreateController().Vote(7, new VoteRequest(value, previous), CancellationToken.None));

        Assert.Equal(21, json.GetProperty("up").GetInt32());
        Assert.Equal(4, json.GetProperty("down").GetInt32());
        Assert.Equal(["up", "down"], json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Vote_WithoutAChange_OnlyReadsTheCounts()
    {
        jokeRepositoryMock.Setup(r => r.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(new Joke { Id = 7, Up = 3, Down = 1 });

        var json = Json(await CreateController().Vote(7, new VoteRequest(1, 1), CancellationToken.None));

        Assert.Equal(3, json.GetProperty("up").GetInt32());
        jokeRepositoryMock.Verify(r => r.AddVotesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, -2)]
    public async Task Vote_RejectsValuesOutsideMinusOneToOne(int value, int previous)
        => Assert.IsType<BadRequestObjectResult>(await CreateController().Vote(7, new VoteRequest(value, previous), CancellationToken.None));

    [Fact]
    public async Task Vote_ForAMissingJoke_ReturnsNotFound()
    {
        jokeRepositoryMock.Setup(r => r.AddVotesAsync(99, 1, 0, It.IsAny<CancellationToken>())).ReturnsAsync((Joke?)null);

        Assert.IsType<NotFoundResult>(await CreateController().Vote(99, new VoteRequest(1, 0), CancellationToken.None));
    }
}
