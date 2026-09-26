using System.Diagnostics;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class JokeSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<JokeGenerationOptions> options;
    private readonly SchedulerStatus status;
    private readonly SchedulerMetrics metrics;
    private readonly ILogger<JokeSchedulerService> logger;
    // Identifies this process in SchedulerLocks: the machine (the Container Apps replica) plus a per-process part.
    private readonly string instanceId =
        $"{(Environment.MachineName.Length > 60 ? Environment.MachineName[..60] : Environment.MachineName)}/{Guid.NewGuid():N}";

    public JokeSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<JokeGenerationOptions> options,
        SchedulerStatus status,
        SchedulerMetrics metrics,
        ILogger<JokeSchedulerService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.status = status;
        this.metrics = metrics;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabledLanguages = options.Value.Languages
            .Where(l => l.Enabled)
            .ToList();

        if (enabledLanguages.Count == 0)
        {
            logger.LogWarning("No languages are enabled for joke generation. Scheduler will not run.");
            return;
        }

        // Every language that will run is due now, before any loop starts: the page's countdown uses
        // the earliest due time, which must stay in the past until every startup tick has completed.
        foreach (var language in enabledLanguages.Where(l => l.LlmModels.Count > 0))
            status.RecordNextTick(language.Language, DateTime.UtcNow);

        // Run one independent loop per language concurrently.
        // WhenAll propagates exceptions but each loop catches its own,
        // so this only completes when all loops exit (i.e. on cancellation).
        await Task.WhenAll(enabledLanguages.Select(lang => RunLoopAsync(lang, stoppingToken)));
    }

    private async Task RunLoopAsync(LanguageOptions language, CancellationToken stoppingToken)
    {
        if (language.LlmModels.Count == 0)
        {
            logger.LogWarning("No LLM models configured for '{Language}'. Skipping.", language.Language);
            return;
        }

        logger.LogInformation("Starting joke scheduler for language '{Language}' (every {Hours}h, {Count} model(s)).",
            language.Language, language.IntervalHours, language.LlmModels.Count);

        // Generate immediately on startup, then on each period.
        await RunTickAsync(language, startup: true, stoppingToken);

        // Ticks are due every period from here (the page's countdown to the next batch). A delay to
        // each due time rather than a PeriodicTimer: a timer keeps a tick that fell due during an
        // overrunning one and fires it at once, while the published due time is already in the future.
        var period = TimeSpan.FromHours(language.IntervalHours);
        var nextTick = DateTime.UtcNow + period;
        status.RecordNextTick(language.Language, nextTick);

        while (true)
        {
            var wait = nextTick - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();

            await RunTickAsync(language, startup: false, stoppingToken);
            // Advance only once the tick (jokes and leaderboard) is done: until then the due time
            // stays in the past, which tells the page to keep polling for the batch. Due times that
            // passed while an overrunning tick ran are skipped, not run back to back.
            do nextTick += period; while (nextTick <= DateTime.UtcNow);
            status.RecordNextTick(language.Language, nextTick);
        }
    }

    /// <summary>
    /// Runs one tick. Any failure (e.g. a transient DB error while ranking the leaderboard) is
    /// logged, so the loop survives and retries on the next tick instead of stopping the host.
    /// </summary>
    internal async Task RunTickAsync(LanguageOptions language, bool startup, CancellationToken stoppingToken)
    {
        // One trace per tick: the lease, the LLM calls, the SQL commands and the judge show up under it.
        using var activity = LazyDadTelemetry.ActivitySource.StartActivity("joke tick");
        activity?.SetTag("language", language.Language);
        activity?.SetTag("startup", startup);
        try
        {
            if (!await TakeTurnAsync(language, startup, stoppingToken))
            {
                status.Record(new TickStatus(language.Language, DateTime.UtcNow, true, [], "skipped", null));
                metrics.RecordTick(language.Language, "skipped");
                logger.LogInformation("Skipped the '{Language}' tick: another replica generated this period.", language.Language);
                return;
            }

            var (saved, leaderboard) = await GenerateAndPersistAsync(language, stoppingToken);
            status.Record(new TickStatus(
                language.Language, DateTime.UtcNow, true,
                saved.Select(j => new GeneratedJoke(j.Id, j.Model)).ToList(), leaderboard, null));
            metrics.RecordTick(language.Language, "succeeded");
            metrics.RecordLeaderboard(language.Language, leaderboard);
            logger.LogDebug("Joke tick for '{Language}' completed.", language.Language);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status.Record(new TickStatus(language.Language, DateTime.UtcNow, false, [], "unknown", ex.GetType().Name));
            metrics.RecordTick(language.Language, "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.AddException(ex);
            logger.LogError(ex, "Joke tick for '{Language}' failed; will retry on the next tick.", language.Language);
        }
    }

    /// <summary>
    /// One batch per language per period across all replicas: a lease in <c>SchedulerLocks</c> that lasts
    /// until just before the next due time. A replica whose timer fires while another holds it skips that
    /// tick; if the holder is gone, the next replica whose timer fires takes over. The startup tick always
    /// runs and takes the lease: a new revision proves itself with it (the deploy's smoke tests check it),
    /// and it usually starts while the old revision still holds the lease.
    /// </summary>
    private async Task<bool> TakeTurnAsync(LanguageOptions language, bool startup, CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromHours(language.IntervalHours);
        // Ends a little early, so the holder's own next due time finds it expired.
        var margin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(5).Ticks, period.Ticks / 10));
        var now = DateTime.UtcNow;
        var lockKey = $"jokes:{language.Language}";

        using var scope = scopeFactory.CreateScope();
        var locks = scope.ServiceProvider.GetRequiredService<ISchedulerLockRepository>();
        if (startup)
        {
            await locks.AcquireAsync(lockKey, instanceId, now, now + period - margin, stoppingToken);
            return true;
        }
        return await locks.TryAcquireAsync(lockKey, instanceId, now, now + period - margin, stoppingToken);
    }

    private async Task<(IReadOnlyList<Joke> Saved, string Leaderboard)> GenerateAndPersistAsync(LanguageOptions language, CancellationToken stoppingToken)
    {
        // All models for the language are queried in parallel.
        var generated = await Task.WhenAll(language.LlmModels.Select(model => GenerateAsync(language, model, stoppingToken)));

        using var scope = scopeFactory.CreateScope();
        var jokeRepository = scope.ServiceProvider.GetRequiredService<IJokeRepository>();
        var topJokeService = scope.ServiceProvider.GetRequiredService<TopJokeService>();

        var saved = new List<Joke>();

        foreach (var joke in generated.OfType<Joke>())
        {
            try
            {
                await jokeRepository.AddAsync(joke, stoppingToken);
                saved.Add(joke);
                metrics.RecordJoke(joke.Language, joke.Model, "saved");

                logger.LogInformation("Joke saved for '{Language}' ({Model}): {Text}", joke.Language, joke.Model, joke.Text);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                metrics.RecordJoke(joke.Language, joke.Model, "failed");
                logger.LogError(ex, "Failed to persist joke for '{Language}' ({Model}).", joke.Language, joke.Model);
            }
        }

        // Runs even when nothing new was saved, so an empty leaderboard still gets seeded.
        var leaderboard = "unchanged";
        try
        {
            if (await topJokeService.UpdateAsync(language.Language, saved, stoppingToken))
                leaderboard = "updated";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            leaderboard = "failed";
            logger.LogError(ex, "Failed to update the top jokes for '{Language}'.", language.Language);
        }

        return (saved, leaderboard);
    }

    /// <summary>Generates one joke with one model. Returns <c>null</c> on failure so sibling models are unaffected.</summary>
    private async Task<Joke?> GenerateAsync(LanguageOptions language, LlmModelOptions model, CancellationToken stoppingToken)
    {
        // Own scope per model: parallel calls must not share a DbContext.
        using var scope = scopeFactory.CreateScope();
        var generationService = scope.ServiceProvider.GetRequiredService<JokeGenerationService>();

        try
        {
            logger.LogInformation("Generating joke for '{Language}' using {Model}...", language.Language, model.Model);

            var text = await generationService.GenerateAsync(language, model, stoppingToken);

            if (string.IsNullOrWhiteSpace(text))
            {
                metrics.RecordJoke(language.Language, model.Model, "empty");
                logger.LogWarning("LLM returned an empty response for '{Language}' ({Model}). Skipping.", language.Language, model.Model);
                return null;
            }

            return new Joke
            {
                Language = language.Language,
                Model = model.Model,
                Text = text,
                GeneratedAt = DateTime.UtcNow
            };
        }
        // Only shutdown propagates. A provider timeout is also an OperationCanceledException;
        // rethrowing it would fault Task.WhenAll and discard the sibling models' jokes.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordJoke(language.Language, model.Model, "failed");
            logger.LogError(ex, "Failed to generate joke for '{Language}' ({Model}).", language.Language, model.Model);
            return null;
        }
    }
}
