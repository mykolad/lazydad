using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

/// <summary>
/// Drives the real scheduler through one tick, with real generation/leaderboard services
/// resolved from DI scopes, and mocks only at the edges (repositories, LLM clients).
/// The interval is an hour, so only the immediate startup tick runs in a test.
/// </summary>
public sealed class JokeSchedulerServiceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly Mock<ITopJokeRepository> topJokeRepositoryMock = new();
    private readonly Mock<ILlmClientFactory> llmClientFactoryMock = new();
    private readonly List<Joke> saved = [];
    // Tests wait on the scheduler's own "tick completed" / "tick failed" logs: they are
    // written after all tick work, so assertions never race the background loop.
    private readonly CapturingLogger<JokeSchedulerService> schedulerLogger = new();
    private readonly SchedulerStatus status = new();
    private ServiceProvider? provider;
    // Leaving TopJokeService out of DI makes the whole tick throw, not just one step of it.
    private bool omitTopJokeService;

    public JokeSchedulerServiceTests()
    {
        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        jokeRepositoryMock
            .Setup(r => r.AddAsync(It.IsAny<Joke>(), It.IsAny<CancellationToken>()))
            .Callback<Joke, CancellationToken>((joke, _) => { lock (saved) { joke.Id = saved.Count + 1; saved.Add(joke); } })
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => provider?.Dispose();

    private static LanguageOptions Ukrainian(params string[] models)
        => new()
        {
            Language = "Ukrainian",
            LanguageCode = "uk",
            Enabled = true,
            IntervalHours = 1,
            LlmModels = models.Select(m => new LlmModelOptions { Provider = "AzureOpenAI", Model = m }).ToList()
        };

    private JokeSchedulerService CreateScheduler(params LanguageOptions[] languages)
    {
        var options = Options.Create(new JokeGenerationOptions { UniquenessSampleSize = 20, Languages = [.. languages] });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton(Options.Create(new TopJokesOptions { Enabled = false }));
        services.AddSingleton(jokeRepositoryMock.Object);
        services.AddSingleton(topJokeRepositoryMock.Object);
        services.AddSingleton(llmClientFactoryMock.Object);
        services.AddScoped<JokeGenerationService>();
        if (!omitTopJokeService)
            services.AddScoped<TopJokeService>();
        provider = services.BuildServiceProvider();

        return new JokeSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), options, status, schedulerLogger);
    }

    private Task TickCompletedAsync() => schedulerLogger.WaitForAsync(LogLevel.Debug, "tick for 'Ukrainian' completed", Timeout);

    private void SetupModel(string model, Func<Task<ChatResponse>> reply)
    {
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(reply);
        llmClientFactoryMock.Setup(f => f.CreateClient("AzureOpenAI", model)).Returns(client.Object);
    }

    private static Task<ChatResponse> Reply(string text)
        => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));

    [Fact]
    public async Task Tick_WhenOneModelTimesOut_SavesTheOtherModelsJoke()
    {
        SetupModel("fast", () => Reply("Швидкий жарт"));
        // A provider timeout is an OperationCanceledException that isn't caused by shutdown.
        SetupModel("slow", () => Task.FromException<ChatResponse>(new TaskCanceledException("HTTP timeout")));
        var scheduler = CreateScheduler(Ukrainian("fast", "slow"));

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        await scheduler.StopAsync(CancellationToken.None);

        var joke = Assert.Single(saved);
        Assert.Equal("fast", joke.Model);
        Assert.Equal("Ukrainian", joke.Language);

        // /status reports exactly what this process saved: the timed-out model is absent.
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Equal([new GeneratedJoke(joke.Id, "fast")], tick.Jokes);
        Assert.Equal("unchanged", tick.Leaderboard);
        Assert.Null(tick.Error);
    }

    [Fact]
    public async Task Tick_WhenModelReturnsEmptyText_SavesNothing()
    {
        SetupModel("blank", () => Reply("   "));
        var scheduler = CreateScheduler(Ukrainian("blank"));

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Contains(schedulerLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("empty response"));
        Assert.Empty(saved);
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Empty(tick.Jokes);
    }

    [Fact]
    public async Task Tick_WhenTheTickFails_LoopSurvives()
    {
        SetupModel("fast", () => Reply("Жарт"));
        omitTopJokeService = true;
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        // Logged by the tick's catch block, i.e. after the exception has been handled.
        await schedulerLogger.WaitForAsync(LogLevel.Error, "tick for 'Ukrainian' failed", Timeout);

        Assert.DoesNotContain(schedulerLogger.Entries, e => e.Message.Contains("tick for 'Ukrainian' completed"));
        await scheduler.StopAsync(CancellationToken.None);

        // The error log is written inside the catch block, so "not completed" right after it
        // proves little. StopAsync doesn't rethrow a faulted ExecuteTask either. The terminal
        // state does prove it: a loop that survived ends cancelled by shutdown, never faulted.
        Assert.True(scheduler.ExecuteTask!.IsCanceled, $"Expected Canceled, was {scheduler.ExecuteTask.Status}.");

        // Recorded as a failed tick with only the exception type (/status is public).
        var tick = Assert.Single(status.LastTicks);
        Assert.False(tick.Succeeded);
        Assert.Equal("InvalidOperationException", tick.Error);
        Assert.Empty(tick.Jokes);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Start_AfterTheStartupTick_SchedulesTheNextOneAnIntervalLater()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var started = DateTime.UtcNow;

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        // The loop records the next tick right after the startup tick completes.
        var deadline = DateTime.UtcNow + Timeout;
        while (status.NextTickAt is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await scheduler.StopAsync(CancellationToken.None);

        var next = Assert.NotNull(status.NextTickAt);
        Assert.InRange(next, started.AddHours(1), DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task Start_WithNoEnabledLanguages_ExitsWithoutGenerating()
    {
        var disabled = Ukrainian("fast");
        disabled.Enabled = false;
        var scheduler = CreateScheduler(disabled);

        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.ExecuteTask!.WaitAsync(Timeout);

        Assert.True(scheduler.ExecuteTask.IsCompletedSuccessfully);
        llmClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Start_WithLanguageWithoutModels_SkipsIt()
    {
        var scheduler = CreateScheduler(Ukrainian());

        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.ExecuteTask!.WaitAsync(Timeout);

        Assert.True(scheduler.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(saved);
    }
}
