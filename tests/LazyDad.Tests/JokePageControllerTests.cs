using LazyDad.Api.Configuration;
using LazyDad.Api.Controllers;
using LazyDad.Api.Services;
using LazyDad.Api.SignIn;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public sealed class JokePageControllerTests : IDisposable
{
    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly ServiceProvider services;
    private readonly JokeReadCache cache;

    public JokePageControllerTests()
    {
        services = new ServiceCollection().AddSingleton(jokeRepositoryMock.Object).BuildServiceProvider();
        cache = new JokeReadCache(new ConfigurationBuilder().Build(), services.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        cache.Dispose();
        services.Dispose();
    }

    private JokePageController CreateController()
    {
        var html = new HtmlGeneratorService(
            Options.Create(new JokeGenerationOptions { Languages = [new() { Language = "Ukrainian", LanguageCode = "uk" }] }),
            Options.Create(new AppInfoOptions { Version = "e33d99a" }),
            new EnabledSignInProviders(["github"]),
            Mock.Of<IWebHostEnvironment>(),
            NullLogger<HtmlGeneratorService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("lazydad.fyi");
        return new JokePageController(cache, html) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    [Fact]
    public async Task Get_ServesTheShell_WithTheJokeInTheTitleAndPreviewTags()
    {
        jokeRepositoryMock.Setup(r => r.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Joke { Id = 42, Language = "Ukrainian", Text = "Чому кава бадьора? Бо зерно!" });

        var result = await CreateController().Get(42, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("<title>Чому кава бадьора? Бо зерно! — LazyDad</title>", result.Content);
        Assert.Contains("<meta property=\"og:url\" content=\"https://lazydad.fyi/j/42\">", result.Content);
        Assert.Contains("id=\"ld-config\"", result.Content);
    }

    [Fact]
    public async Task Get_ForAMissingJoke_ServesTheShellWithNotFound()
    {
        jokeRepositoryMock.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Joke?)null);

        var result = await CreateController().Get(99, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Contains("<title>LazyDad</title>", result.Content);
        Assert.Contains("id=\"ld-config\"", result.Content);
    }
}
