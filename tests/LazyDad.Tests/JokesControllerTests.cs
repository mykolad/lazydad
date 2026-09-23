using System.Text.Json;
using LazyDad.Api.Controllers;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace LazyDad.Tests;

public class JokesControllerTests
{
    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly Mock<ITopJokeRepository> topJokeRepositoryMock = new();

    private JokesController CreateController() => new(jokeRepositoryMock.Object, topJokeRepositoryMock.Object);

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
        topJokeRepositoryMock.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new TopJoke { Language = "Ukrainian", Rank = 1, JokeId = 5, Joke = joke, Reason = "Clever", JudgeModel = "gpt-6-sol" }
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
        Assert.Equal("Joke 5", entry.GetProperty("joke").GetProperty("text").GetString());
    }
}
