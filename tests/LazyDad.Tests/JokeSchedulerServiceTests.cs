using System.Diagnostics;
using System.Diagnostics.Metrics;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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
    // Loose mock: AcquireAsync completes, TryAcquireAsync returns false unless a test sets it up.
    private readonly Mock<ISchedulerLockRepository> lockRepositoryMock = new();
    private readonly Mock<ILlmClientFactory> llmClientFactoryMock = new();
    private readonly List<Joke> saved = [];
    // Tests wait on the scheduler's own "tick completed" / "tick failed" logs: they are
    // written after all tick work, so assertions never race the background loop.
    private readonly CapturingLogger<JokeSchedulerService> schedulerLogger = new();
    private readonly SchedulerStatus status = new();
    private readonly ServiceProvider metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
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

    public void Dispose()
    {
        provider?.Dispose();
        metricsProvider.Dispose();
    }

    /// <summary>Collects one of the scheduler's counters (only this test's meter factory, so parallel tests don't mix in).</summary>
    private MetricCollector<long> Collect(string instrument)
        => new(metricsProvider.GetRequiredService<IMeterFactory>(), LazyDadTelemetry.Name, instrument);

    /// <summary>Each measurement's tag values, e.g. "fast/saved" for the tags model and outcome, in order.</summary>
    private static IEnumerable<string> Measured(MetricCollector<long> collector, params string[] tags)
        => collector.GetMeasurementSnapshot()
            .Select(m => string.Join('/', tags.Select(t => m.Tags[t])))
            .Order(StringComparer.Ordinal);

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
        services.AddSingleton(lockRepositoryMock.Object);
        services.AddSingleton(llmClientFactoryMock.Object);
        services.AddScoped<JokeGenerationService>();
        if (!omitTopJokeService)
            services.AddScoped<TopJokeService>();
        provider = services.BuildServiceProvider();

        return new JokeSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(), options, status,
            new SchedulerMetrics(metricsProvider.GetRequiredService<IMeterFactory>()), schedulerLogger);
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
        using var jokes = Collect("lazydad.jokes");
        using var ticks = Collect("lazydad.scheduler.ticks");
        using var leaderboard = Collect("lazydad.leaderboard.updates");

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

        // The metrics count the same outcomes, for charts and alerts.
        Assert.Equal(["Ukrainian/fast/saved", "Ukrainian/slow/failed"], Measured(jokes, "language", "model", "outcome"));
        Assert.Equal(["Ukrainian/succeeded"], Measured(ticks, "language", "outcome"));
        Assert.Equal(["Ukrainian/unchanged"], Measured(leaderboard, "language", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenModelReturnsEmptyText_SavesNothing()
    {
        SetupModel("blank", () => Reply("   "));
        var scheduler = CreateScheduler(Ukrainian("blank"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Contains(schedulerLogger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("empty response"));
        Assert.Empty(saved);
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Empty(tick.Jokes);
        Assert.Equal(["blank/empty"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenSavingAJokeFails_CountsItAsFailed()
    {
        SetupModel("fast", () => Reply("Жарт"));
        jokeRepositoryMock
            .Setup(r => r.AddAsync(It.IsAny<Joke>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Contains(schedulerLogger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed to persist joke"));
        Assert.Empty(Assert.Single(status.LastTicks).Jokes);
        Assert.Equal(["fast/failed"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenTheTickFails_LoopSurvives()
    {
        SetupModel("fast", () => Reply("Жарт"));
        omitTopJokeService = true;
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var ticks = Collect("lazydad.scheduler.ticks");
        using var leaderboard = Collect("lazydad.leaderboard.updates");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LazyDadTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) spans.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);

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

        // Counted as failed, and the tick's trace is marked as an error. The leaderboard never ran.
        Assert.Equal(["Ukrainian/failed"], Measured(ticks, "language", "outcome"));
        Assert.Empty(leaderboard.GetMeasurementSnapshot());
        Activity span;
        lock (spans) span = Assert.Single(spans, s => s.OperationName == "joke tick");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Ukrainian", span.GetTagItem("language"));
        Assert.Equal(true, span.GetTagItem("startup"));
        Assert.Contains(span.Events, e => e.Name == "exception");
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
        await NextTickAfterAsync(started.AddMinutes(30));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.InRange(status.NextTickAt!.Value, started.AddHours(1), DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public async Task Start_KeepsTheNextTickDue_UntilEveryLanguagesStartupTickCompletes()
    {
        var slowReply = new TaskCompletionSource<ChatResponse>();
        SetupModel("fast", () => Reply("Жарт"));
        SetupModel("slow", () => slowReply.Task);
        var english = Ukrainian("slow");
        english.Language = "English";
        english.LanguageCode = "en";
        var scheduler = CreateScheduler(Ukrainian("fast"), english);
        var started = DateTime.UtcNow;

        await scheduler.StartAsync(CancellationToken.None);
        await TickCompletedAsync();
        await Task.Delay(50);

        // Ukrainian has scheduled its next tick, but English is still generating: the page must
        // keep polling, so the earliest due time is still the past.
        Assert.True(status.NextTickAt <= DateTime.UtcNow, $"NextTickAt was {status.NextTickAt:o}.");

        slowReply.SetResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Joke")]));
        await schedulerLogger.WaitForAsync(LogLevel.Debug, "tick for 'English' completed", Timeout);
        await NextTickAfterAsync(started.AddMinutes(30));
        await scheduler.StopAsync(CancellationToken.None);
    }

    private async Task NextTickAfterAsync(DateTime threshold)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!(status.NextTickAt > threshold) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(status.NextTickAt > threshold, $"NextTickAt was {status.NextTickAt:o}, expected after {threshold:o}.");
    }

    [Fact]
    public async Task StartupTick_TakesTheLease_EvenIfAnotherReplicaHoldsIt()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var before = DateTime.UtcNow;

        await scheduler.RunTickAsync(Ukrainian("fast"), true, CancellationToken.None);

        Assert.Single(saved);
        // Until just before the next due time: 1h minus a margin (a tenth of the period, at most 5 min).
        lockRepositoryMock.Verify(r => r.AcquireAsync(
            "jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(),
            It.Is<DateTime>(expires => expires >= before.AddMinutes(55) && expires <= DateTime.UtcNow.AddMinutes(55)),
            It.IsAny<CancellationToken>()), Times.Once);
        lockRepositoryMock.Verify(r => r.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PeriodicTick_WhenAnotherReplicaHoldsTheLease_SkipsWithoutCallingTheModels()
    {
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(r => r.TryAcquireAsync("jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var ticks = Collect("lazydad.scheduler.ticks");
        using var leaderboard = Collect("lazydad.leaderboard.updates");

        await scheduler.RunTickAsync(Ukrainian("fast"), false, CancellationToken.None);

        Assert.Empty(saved);
        llmClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Equal("skipped", tick.Leaderboard);
        Assert.Empty(tick.Jokes);
        Assert.Equal(["Ukrainian/skipped"], Measured(ticks, "language", "outcome"));
        Assert.Empty(leaderboard.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task PeriodicTick_WithTheLease_Generates()
    {
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(r => r.TryAcquireAsync("jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.RunTickAsync(Ukrainian("fast"), false, CancellationToken.None);

        Assert.Single(saved);
        Assert.Equal("unchanged", Assert.Single(status.LastTicks).Leaderboard);
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
