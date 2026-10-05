using System.Diagnostics;
using System.Diagnostics.Metrics;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    // Profiles nothing unless a test sets it up.
    private readonly Mock<IJokeProfiler> profilerMock = new();
    private readonly List<Joke> saved = [];
    private readonly SchedulerStatus status = new();
    private readonly JokeReadCache readCache = new(new ConfigurationBuilder().Build(), new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    private readonly ServiceProvider metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private ServiceProvider? provider;
    // Leaving TopJokeService out of DI makes the whole tick throw, not just one step of it.
    private bool omitTopJokeService;
    // The leaderboard is off unless a test turns it on (then the judge is the "judge" model).
    private TopJokesOptions topJokesOptions = new() { Enabled = false };

    public JokeSchedulerServiceTests()
    {
        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        jokeRepositoryMock
            .Setup(r => r.AddAsync(It.IsAny<Joke>(), It.IsAny<CancellationToken>()))
            .Callback<Joke, CancellationToken>((joke, _) => { lock (saved) { joke.Id = saved.Count + 1; saved.Add(joke); } })
            .Returns(Task.CompletedTask);
        // Every saved joke still exists, unless a test says another replica deleted one.
        jokeRepositoryMock
            .Setup(r => r.GetExistingIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => ids.ToList());
        // This replica gets the duplicates cleanup's lease, and there are no duplicates to remove, unless a test says otherwise.
        lockRepositoryMock
            .Setup(l => l.TryAcquireAsync(It.Is<string>(k => k.StartsWith("duplicates:")), It.IsAny<string>(), It.IsAny<DateTime>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    public void Dispose()
    {
        provider?.Dispose();
        metricsProvider.Dispose();
        readCache.Dispose();
    }

    /// <summary>Collects one of the scheduler's counters (only this test's meter factory, so parallel tests don't mix in).</summary>
    private MetricCollector<long> Collect(string instrument)
        => new(metricsProvider.GetRequiredService<IMeterFactory>(), LazyDadTelemetry.Name, instrument);

    /// <summary>
    /// Each counted measurement's tag values, e.g. "fast/saved" for the tags model and outcome, in order. The zeros
    /// the scheduler records at startup (<see cref="SchedulerMetrics.Initialize"/>) count nothing, so they're left out.
    /// </summary>
    private static IEnumerable<string> Measured(MetricCollector<long> collector, params string[] tags)
        => collector.GetMeasurementSnapshot()
            .Where(m => m.Value != 0)
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
        // Retries without waiting: a test's failing model is asked again at once.
        var options = Options.Create(new JokeGenerationOptions { UniquenessSampleSize = 20, RetryDelaySeconds = 0, Languages = [.. languages] });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton(Options.Create(topJokesOptions));
        services.AddSingleton(jokeRepositoryMock.Object);
        services.AddSingleton(topJokeRepositoryMock.Object);
        services.AddSingleton(lockRepositoryMock.Object);
        services.AddSingleton(llmClientFactoryMock.Object);
        services.AddSingleton(profilerMock.Object);
        services.AddScoped<JokeGenerationService>();
        if (!omitTopJokeService)
            services.AddScoped<TopJokeService>();
        provider = services.BuildServiceProvider();

        return new JokeSchedulerService(
            provider.GetRequiredService<IServiceScopeFactory>(), options, status,
            new SchedulerMetrics(metricsProvider.GetRequiredService<IMeterFactory>()), readCache, NullLogger<JokeSchedulerService>.Instance);
    }

    /// <summary>
    /// Waits until every language's startup tick is done, so assertions never race the background loop. The scheduler
    /// moves the next due time into the future only after a tick's jokes, leaderboard and metrics are all recorded
    /// (until then, the page keeps polling for the batch). At start it records "due now", which since .NET 10
    /// (ExecuteAsync runs on a background thread) can happen after this reads the clock: so wait for a due time at
    /// least a minute ahead, which only a finished startup tick sets (its next tick is half a period away or more).
    /// </summary>
    private Task StartupTicksDoneAsync() => NextTickAfterAsync(DateTime.UtcNow.AddMinutes(1));

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
        await StartupTicksDoneAsync();
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
    public async Task Tick_WhenTheJudgeFails_KeepsTheJokes_AndCountsTheLeaderboardAsFailed()
    {
        SetupModel("fast", () => Reply("Жарт"));
        SetupModel("judge", () => Task.FromException<ChatResponse>(new HttpRequestException("The judge is unavailable.")));
        topJokesOptions = new TopJokesOptions
        {
            Enabled = true,
            Judge = new LlmModelOptions { Provider = "AzureOpenAI", Model = "judge" },
        };
        // An empty leaderboard is seeded from the recent jokes: one is enough for the judge to be asked.
        topJokeRepositoryMock
            .Setup(r => r.GetByLanguageAsync("Ukrainian", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Ukrainian", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Joke { Id = 7, Language = "Ukrainian", Model = "fast", Text = "Старий жарт" }]);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var leaderboard = Collect("lazydad.leaderboard.updates");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        // The tick itself succeeds: the new joke is saved, and only the leaderboard update failed.
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Equal("failed", tick.Leaderboard);
        Assert.Single(saved);
        Assert.Equal(["failed"], Measured(leaderboard, "outcome"));
    }

    [Fact]
    public async Task Start_StartsEverySeriesAtZero_SoTheFirstTickCounts()
    {
        SetupModel("fast", () => Reply("Швидкий жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var jokes = Collect("lazydad.jokes");
        using var ticks = Collect("lazydad.scheduler.ticks");
        using var leaderboard = Collect("lazydad.leaderboard.updates");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        // Every series exists at 0 before the startup tick adds to it, so increase() sees that tick too.
        static IEnumerable<string> Zeros(MetricCollector<long> collector, params string[] tags)
            => collector.GetMeasurementSnapshot()
                .Where(m => m.Value == 0)
                .Select(m => string.Join('/', tags.Select(t => m.Tags[t])))
                .Order(StringComparer.Ordinal);
        Assert.Equal(["fast/duplicate", "fast/empty", "fast/failed", "fast/saved"], Zeros(jokes, "model", "outcome"));
        Assert.Equal(["failed", "skipped", "succeeded"], Zeros(ticks, "outcome"));
        Assert.Equal(["failed", "unchanged", "updated"], Zeros(leaderboard, "outcome"));
        // Recorded before the tick: the first measurement is a zero, and the tick then counts once.
        Assert.Equal(0, ticks.GetMeasurementSnapshot()[0].Value);
        Assert.Equal(["succeeded"], Measured(ticks, "outcome"));
    }

    [Fact]
    public async Task Tick_WhenAModelFailsOnce_TriesAgain_AndSavesItsJoke()
    {
        var tries = 0;
        SetupModel("flaky", () => ++tries == 1
            ? Task.FromException<ChatResponse>(new InvalidOperationException("503 Service Unavailable"))
            : Reply("Жарт з другої спроби"));
        var scheduler = CreateScheduler(Ukrainian("flaky"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(2, tries);
        Assert.Equal("Жарт з другої спроби", Assert.Single(saved).Text);
        // Only the outcome counts: a joke saved on a later try isn't a failure.
        Assert.Equal(["flaky/saved"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenAModelAnswersEmptyTwice_SavesItsThirdAnswer()
    {
        var tries = 0;
        SetupModel("thinking", () => Reply(++tries < 3 ? " " : "Нарешті жарт"));
        var scheduler = CreateScheduler(Ukrainian("thinking"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(3, tries);
        Assert.Equal("Нарешті жарт", Assert.Single(saved).Text);
    }

    [Fact]
    public async Task Tick_WhenAModelFailsEveryTry_GivesUpAfterThree_AndCountsOneFailure()
    {
        var tries = 0;
        SetupModel("down", () => { tries++; return Task.FromException<ChatResponse>(new InvalidOperationException("down")); });
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("down", "fast"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(3, tries);
        Assert.Equal("fast", Assert.Single(saved).Model);
        Assert.Equal(["down/failed", "fast/saved"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenModelReturnsEmptyText_SavesNothing()
    {
        SetupModel("blank", () => Reply("   "));
        var scheduler = CreateScheduler(Ukrainian("blank"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(saved);
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Empty(tick.Jokes);
        Assert.Equal(["blank/empty"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_SavesTheJokesExplanationWithIt()
    {
        SetupModel("fast", () => Reply("— Чому годинник пішов?\n— Бо мав багато часу!\n---\nA pun on \"пішов\"."));
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        var joke = Assert.Single(saved);
        Assert.Equal("— Чому годинник пішов?\n— Бо мав багато часу!", joke.Text);
        Assert.Equal("A pun on \"пішов\".", joke.Explanation);
    }

    [Fact]
    public async Task Tick_ThatSavesAJoke_DropsThisReplicasCachedReads()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        await readCache.GetOrLoadAsync("count", (_, _) => Task.FromResult(1), CancellationToken.None);

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        // The page's next reads come from the database, with the new joke, not from a copy made before it.
        Assert.Equal(2, await readCache.GetOrLoadAsync("count", (_, _) => Task.FromResult(2), CancellationToken.None));
    }

    [Fact]
    public async Task Tick_ThatChangesNothing_KeepsTheCachedReads()
    {
        SetupModel("blank", () => Reply("   "));
        var scheduler = CreateScheduler(Ukrainian("blank"));
        await readCache.GetOrLoadAsync("count", (_, _) => Task.FromResult(1), CancellationToken.None);

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(1, await readCache.GetOrLoadAsync("count", (_, _) => Task.FromResult(2), CancellationToken.None));
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
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

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
        // The failed startup tick is handled, and the loop schedules the next one.
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        // StopAsync doesn't rethrow a faulted ExecuteTask. The terminal state proves the loop survived: it ends
        // cancelled by shutdown, never faulted.
        Assert.True(scheduler.ExecuteTask!.IsCanceled, $"Expected Canceled, was {scheduler.ExecuteTask.Status}.");

        // Recorded as a failed tick with only the exception type (/status is public).
        var tick = Assert.Single(status.LastTicks);
        Assert.False(tick.Succeeded);
        Assert.Equal("InvalidOperationException", tick.Error);
        Assert.Empty(tick.Jokes);
        // The joke was saved as soon as its model answered, before the leaderboard step failed, and it stays saved.
        Assert.Equal("fast", Assert.Single(saved).Model);

        // Counted as failed, and the tick's trace is marked as an error. The leaderboard never ran.
        Assert.Equal(["Ukrainian/failed"], Measured(ticks, "language", "outcome"));
        Assert.Empty(Measured(leaderboard, "outcome"));
        Activity span;
        lock (spans) span = Assert.Single(spans, s => s.OperationName == "joke tick");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Ukrainian", span.GetTagItem("language"));
        Assert.Equal(true, span.GetTagItem("startup"));
        Assert.Contains(span.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task Start_AfterTheStartupTick_SchedulesTheNextRegularTickOnTheFixedGrid()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var period = TimeSpan.FromHours(1);
        var started = DateTime.UtcNow;

        await scheduler.StartAsync(CancellationToken.None);
        // The loop records the next tick right after the startup tick completes.
        await NextTickAfterAsync(started.AddMinutes(29));
        await scheduler.StopAsync(CancellationToken.None);

        // A whole hour (the interval here), at least half an hour after the restart: not "an interval after the
        // restart", so restarts don't move the rhythm. (Either instant may be the one the scheduler read.)
        var next = status.NextTickAt!.Value;
        Assert.Contains(next, new[] { TickSchedule.FirstDueAfterStartup(started, period), TickSchedule.FirstDueAfterStartup(DateTime.UtcNow, period) });
        Assert.Equal(0, next.Ticks % period.Ticks);
    }

    [Fact]
    public async Task Tick_SavesEachJokeAsSoonAsItsModelAnswers_WithoutWaitingForTheSlowOne()
    {
        var slowReply = new TaskCompletionSource<ChatResponse>();
        SetupModel("fast", () => Reply("Швидкий жарт"));
        SetupModel("slow", () => slowReply.Task);
        var scheduler = CreateScheduler(Ukrainian("fast", "slow"));

        await scheduler.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow + Timeout;
        while (status.SavedJokes.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        // The slow model is still working, yet the fast one's joke is saved and listed on /status already.
        var early = Assert.Single(status.SavedJokes);
        Assert.Equal(("Ukrainian", "fast"), (early.Language, early.Model));
        Assert.Equal(early.Id, Assert.Single(saved).Id);
        Assert.Empty(status.LastTicks);

        slowReply.SetResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Повільний жарт")]));
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(["fast", "slow"], status.SavedJokes.Select(j => j.Model).Order());
        Assert.Equal(2, Assert.Single(status.LastTicks).Jokes.Count);
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
        await TickRecordedAsync("Ukrainian");
        await Task.Delay(50);

        // Ukrainian has scheduled its next tick, but English is still generating: the page must
        // keep polling, so the earliest due time is still the past.
        Assert.True(status.NextTickAt <= DateTime.UtcNow, $"NextTickAt was {status.NextTickAt:o}.");

        slowReply.SetResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Joke")]));
        await NextTickAfterAsync(started.AddMinutes(30));
        await scheduler.StopAsync(CancellationToken.None);
    }

    /// <summary>Waits until <paramref name="language"/>'s tick result is in <see cref="SchedulerStatus"/> (as on /status).</summary>
    private async Task TickRecordedAsync(string language)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!status.LastTicks.Any(t => t.Language == language) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains(status.LastTicks, t => t.Language == language);
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
        var nextDue = TickSchedule.FirstDueAfterStartup(DateTime.UtcNow, TimeSpan.FromHours(1));

        await scheduler.RunTickAsync(Ukrainian("fast"), true, nextDue, CancellationToken.None);

        Assert.Single(saved);
        // Until just before the next due time: minus a margin (a tenth of the 1h period, at most 5 min). Every
        // replica computes the same due times, so their ticks until then find the lease held.
        lockRepositoryMock.Verify(r => r.AcquireAsync(
            "jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), nextDue.AddMinutes(-5),
            It.IsAny<CancellationToken>()), Times.Once);
        lockRepositoryMock.Verify(r => r.TryAcquireAsync("jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
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

        await scheduler.RunTickAsync(Ukrainian("fast"), false, DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Empty(saved);
        llmClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Equal("skipped", tick.Leaderboard);
        Assert.Empty(tick.Jokes);
        Assert.Equal(["Ukrainian/skipped"], Measured(ticks, "language", "outcome"));
        Assert.Empty(Measured(leaderboard, "outcome"));
    }

    [Theory]
    [InlineData(3)]    // the lease would already have expired (inside the 5-minute margin)
    [InlineData(20)]   // 15 minutes of lease: it could expire mid-batch and let another replica take the slot
    public async Task PeriodicTick_TooLateForItsSlot_SkipsWithoutTakingAShortLease(int minutesToNextDue)
    {
        // E.g. the host was suspended. With a 1h period, a regular tick needs at least half an hour of lease left.
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(r => r.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.RunTickAsync(Ukrainian("fast"), false, DateTime.UtcNow.AddMinutes(minutesToNextDue), CancellationToken.None);

        Assert.Empty(saved);
        lockRepositoryMock.Verify(r => r.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        llmClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Equal("skipped", Assert.Single(status.LastTicks).Leaderboard);
    }

    [Fact]
    public async Task PeriodicTick_SomewhatLate_StillRunsWithEnoughLease()
    {
        // 40 minutes to the next due time: 35 minutes of lease, over half the 1h period.
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(r => r.TryAcquireAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var nextDue = DateTime.UtcNow.AddMinutes(40);

        await scheduler.RunTickAsync(Ukrainian("fast"), false, nextDue, CancellationToken.None);

        Assert.Single(saved);
        lockRepositoryMock.Verify(r => r.TryAcquireAsync("jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), nextDue.AddMinutes(-5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PeriodicTick_WithTheLease_Generates()
    {
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(r => r.TryAcquireAsync("jokes:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.RunTickAsync(Ukrainian("fast"), false, DateTime.UtcNow.AddHours(1), CancellationToken.None);

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

    [Fact]
    public async Task Tick_ProfilesTheJokesForSimilarJokes_AfterSavingThem()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var savedWhenProfiling = -1;
        profilerMock
            .Setup(p => p.ProfileAsync(It.IsAny<CancellationToken>()))
            .Callback(() => savedWhenProfiling = saved.Count)
            .ReturnsAsync(1);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(1, savedWhenProfiling);
        profilerMock.Verify(p => p.ProfileAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tick_WhenProfilingFails_StillSucceeds()
    {
        SetupModel("fast", () => Reply("Жарт"));
        profilerMock
            .Setup(p => p.ProfileAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database is busy."));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var ticks = Collect("lazydad.scheduler.ticks");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Single(saved);
        Assert.True(Assert.Single(status.LastTicks).Succeeded);
        Assert.Equal(["Ukrainian/succeeded"], Measured(ticks, "language", "outcome"));
    }

    [Fact]
    public async Task Tick_WhenAModelRepeatsAnExistingJoke_AsksAgainAtOnce_NamingIt_AndSavesTheNewOne()
    {
        const string old = "Чому гречка стала бухгалтеркою? Бо рахувала крупні витрати!";
        jokeRepositoryMock.Setup(r => r.TextExistsAsync("Ukrainian", old, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var prompts = new List<string>();
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                prompts.Add(messages.First().Text);
                return Reply(prompts.Count == 1 ? old : "Новий жарт");
            });
        llmClientFactoryMock.Setup(f => f.CreateClient("AzureOpenAI", "fast")).Returns(client.Object);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal("Новий жарт", Assert.Single(saved).Text);
        Assert.Equal(2, prompts.Count);
        Assert.DoesNotContain(old, prompts[0]);
        Assert.Contains($"  * {old}", prompts[1]);
    }

    [Fact]
    public async Task Tick_WhenAModelRepeatsExistingJokesEveryTry_SavesNothing_AndCountsADuplicate()
    {
        jokeRepositoryMock.Setup(r => r.TextExistsAsync("Ukrainian", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        SetupModel("fast", () => Reply("Старий жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(saved);
        Assert.Equal(["fast/duplicate"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_RemovesDuplicateJokes_AndAFailureThereDoesNotFailTheTick()
    {
        SetupModel("fast", () => Reply("Жарт"));
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database is busy."));
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        jokeRepositoryMock.Verify(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(Assert.Single(status.LastTicks).Succeeded);
        Assert.Single(saved);
    }

    [Fact]
    public async Task Tick_WhenATryFailedAndTheLastOneRepeatedAJoke_CountsTheFailure_NotADuplicate()
    {
        // "duplicate" means every try repeated a joke; a real failure among them is what's counted (and alerted).
        var tries = 0;
        SetupModel("fast", () => ++tries == 1
            ? Task.FromException<ChatResponse>(new HttpRequestException("The model is unavailable."))
            : Reply("Старий жарт"));
        jokeRepositoryMock.Setup(r => r.TextExistsAsync("Ukrainian", "Старий жарт", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        using var jokes = Collect("lazydad.jokes");

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(3, tries);
        Assert.Empty(saved);
        Assert.Equal(["fast/failed"], Measured(jokes, "model", "outcome"));
    }

    [Fact]
    public async Task Tick_RemovesDuplicatesBeforeTheJudge_WhichSeesOnlyTheCopyThatWasKept()
    {
        SetupModel("fast", () => Reply("Жарт А"));
        // Answers after "fast", so its joke is saved second (id 2), and the cleanup deletes it.
        SetupModel("slow", async () => { await Task.Delay(300); return await Reply("Жарт А."); });
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([2]);
        var judgePrompts = new List<string>();
        var judge = new Mock<IChatClient>();
        judge
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                judgePrompts.Add(string.Join("\n", messages.Select(m => m.Text)));
                return Task.FromException<ChatResponse>(new HttpRequestException("Only the prompt matters here."));
            });
        llmClientFactoryMock.Setup(f => f.CreateClient("AzureOpenAI", "judge")).Returns(judge.Object);
        topJokesOptions = new TopJokesOptions { Enabled = true, Judge = new LlmModelOptions { Provider = "AzureOpenAI", Model = "judge" } };
        // A full leaderboard: the judge weighs it against this tick's new jokes only.
        topJokeRepositoryMock
            .Setup(r => r.GetByLanguageAsync("Ukrainian", It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(1, 3).Select(rank => new TopJoke
            {
                Language = "Ukrainian", Rank = rank, JokeId = 100 + rank, Reason = "r", JudgeModel = "judge",
                Joke = new Joke { Id = 100 + rank, Language = "Ukrainian", Model = "m", Text = $"Топ {rank}" },
            })]);
        var scheduler = CreateScheduler(Ukrainian("fast", "slow"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(2, saved.Count);
        var prompt = Assert.Single(judgePrompts);
        Assert.Contains("Жарт А", prompt);
        Assert.DoesNotContain("Жарт А.", prompt);
        Assert.Equal([1], Assert.Single(status.LastTicks).Jokes.Select(j => j.Id));
    }

    [Fact]
    public async Task Tick_WhileAnotherReplicaRemovesDuplicates_LeavesItToThatOne_AndReleasesItsOwnLeaseOtherwise()
    {
        SetupModel("fast", () => Reply("Жарт"));
        lockRepositoryMock
            .Setup(l => l.TryAcquireAsync("duplicates:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        jokeRepositoryMock.Verify(r => r.RemoveDuplicatesAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        lockRepositoryMock.Verify(l => l.ReleaseAsync("duplicates:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(saved);
    }

    [Fact]
    public async Task Tick_ReleasesTheDuplicatesLease_AfterTheCleanup()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var scheduler = CreateScheduler(Ukrainian("fast"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        jokeRepositoryMock.Verify(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
        lockRepositoryMock.Verify(l => l.ReleaseAsync("duplicates:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Tick_WhenReleasingTheDuplicatesLeaseFails_StillLeavesOutTheDeletedCopies()
    {
        SetupModel("fast", () => Reply("Жарт А"));
        // Answers after "fast", so its joke is saved second (id 2), and the cleanup deletes it.
        SetupModel("slow", async () => { await Task.Delay(300); return await Reply("Жарт А."); });
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([2]);
        lockRepositoryMock
            .Setup(l => l.ReleaseAsync("duplicates:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database is busy."));
        var scheduler = CreateScheduler(Ukrainian("fast", "slow"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        var tick = Assert.Single(status.LastTicks);
        Assert.True(tick.Succeeded);
        Assert.Equal([1], tick.Jokes.Select(j => j.Id));
    }

    [Fact]
    public async Task Tick_LooksAtEveryJokeOnTheFirstCleanupOfTheProcess_ThenAtTheLastDays()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var since = new List<DateTime?>();
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime?, CancellationToken>((_, from, _) => since.Add(from))
            .ReturnsAsync([]);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var nextDue = TickSchedule.FirstDueAfterStartup(DateTime.UtcNow, TimeSpan.FromHours(1));

        await scheduler.RunTickAsync(Ukrainian("fast"), true, nextDue, CancellationToken.None);
        await scheduler.RunTickAsync(Ukrainian("fast"), true, nextDue, CancellationToken.None);

        Assert.Equal(2, since.Count);
        Assert.Null(since[0]);
        Assert.InRange(since[1]!.Value, DateTime.UtcNow.AddDays(-1).AddMinutes(-1), DateTime.UtcNow.AddDays(-1).AddMinutes(1));
        jokeRepositoryMock.Verify(r => r.FillTextHashesAsync("Ukrainian", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Tick_WhenItsFullPassWasSkipped_TriesTheFullPassAgainNextTick()
    {
        SetupModel("fast", () => Reply("Жарт"));
        var since = new List<DateTime?>();
        jokeRepositoryMock
            .Setup(r => r.RemoveDuplicatesAsync("Ukrainian", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime?, CancellationToken>((_, from, _) => since.Add(from))
            .ReturnsAsync([]);
        var leases = 0;
        // Another replica holds the duplicates lease on this process's first cleanup only.
        lockRepositoryMock
            .Setup(l => l.TryAcquireAsync("duplicates:Ukrainian", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++leases > 1);
        var scheduler = CreateScheduler(Ukrainian("fast"));
        var nextDue = TickSchedule.FirstDueAfterStartup(DateTime.UtcNow, TimeSpan.FromHours(1));

        await scheduler.RunTickAsync(Ukrainian("fast"), true, nextDue, CancellationToken.None);
        await scheduler.RunTickAsync(Ukrainian("fast"), true, nextDue, CancellationToken.None);

        Assert.Equal([null], since);
    }

    [Fact]
    public async Task Tick_WhenAnotherReplicaDeletedASavedCopy_TheJudgeDoesNotSeeIt()
    {
        SetupModel("fast", () => Reply("Жарт А"));
        SetupModel("slow", async () => { await Task.Delay(300); return await Reply("Жарт Б"); });
        // Another replica's cleanup deleted joke 2 (slow's) as a later copy; this replica's own cleanup removed nothing.
        jokeRepositoryMock
            .Setup(r => r.GetExistingIdsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => ids.Where(id => id != 2).ToList());
        var judgePrompts = new List<string>();
        var judge = new Mock<IChatClient>();
        judge
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> messages, ChatOptions? _, CancellationToken _) =>
            {
                judgePrompts.Add(string.Join("\n", messages.Select(m => m.Text)));
                return Task.FromException<ChatResponse>(new HttpRequestException("Only the prompt matters here."));
            });
        llmClientFactoryMock.Setup(f => f.CreateClient("AzureOpenAI", "judge")).Returns(judge.Object);
        topJokesOptions = new TopJokesOptions { Enabled = true, Judge = new LlmModelOptions { Provider = "AzureOpenAI", Model = "judge" } };
        topJokeRepositoryMock
            .Setup(r => r.GetByLanguageAsync("Ukrainian", It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(1, 3).Select(rank => new TopJoke
            {
                Language = "Ukrainian", Rank = rank, JokeId = 100 + rank, Reason = "r", JudgeModel = "judge",
                Joke = new Joke { Id = 100 + rank, Language = "Ukrainian", Model = "m", Text = $"Топ {rank}" },
            })]);
        var scheduler = CreateScheduler(Ukrainian("fast", "slow"));

        await scheduler.StartAsync(CancellationToken.None);
        await StartupTicksDoneAsync();
        await scheduler.StopAsync(CancellationToken.None);

        var prompt = Assert.Single(judgePrompts);
        Assert.Contains("Жарт А", prompt);
        Assert.DoesNotContain("Жарт Б", prompt);
    }
}
