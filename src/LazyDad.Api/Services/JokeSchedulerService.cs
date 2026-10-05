using System.ClientModel;
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
    private readonly JokeReadCache readCache;
    private readonly ILogger<JokeSchedulerService> logger;
    // Identifies this process in SchedulerLocks: the machine (the Container Apps replica) plus a per-process part.
    private readonly string instanceId =
        $"{(Environment.MachineName.Length > 60 ? Environment.MachineName[..60] : Environment.MachineName)}/{Guid.NewGuid():N}";

    public JokeSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<JokeGenerationOptions> options,
        SchedulerStatus status,
        SchedulerMetrics metrics,
        JokeReadCache readCache,
        ILogger<JokeSchedulerService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.status = status;
        this.metrics = metrics;
        this.readCache = readCache;
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
        {
            status.RecordNextTick(language.Language, DateTime.UtcNow);
            metrics.Initialize(language.Language, language.LlmModels.Select(m => m.Model));
        }
        // Grafana must receive those zeros before a tick can add to them (see ExportNowAsync).
        await metrics.ExportNowAsync();

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

        // Generate immediately on startup, then at the regular due times: fixed UTC times (TickSchedule), so a
        // restart adds its startup batch but doesn't move the rhythm. The first regular one is at least half a
        // period away, so a restart just before a due time doesn't make two batches minutes apart.
        var period = TimeSpan.FromHours(language.IntervalHours);
        var nextTick = TickSchedule.FirstDueAfterStartup(DateTime.UtcNow, period);
        await RunTickAsync(language, startup: true, nextTick, stoppingToken);
        // The page's countdown to the next batch.
        status.RecordNextTick(language.Language, nextTick);

        // A delay to each due time rather than a PeriodicTimer: a timer keeps a tick that fell due during
        // an overrunning one and fires it at once, while the published due time is already in the future.
        while (true)
        {
            var wait = nextTick - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, stoppingToken);
            stoppingToken.ThrowIfCancellationRequested();

            await RunTickAsync(language, startup: false, TickSchedule.NextDue(nextTick, period), stoppingToken);
            // Advance only once the tick (jokes and leaderboard) is done: until then the due time
            // stays in the past, which tells the page to keep polling for the batch. Due times that
            // passed while an overrunning tick ran are skipped, not run back to back.
            nextTick = TickSchedule.NextDue(DateTime.UtcNow, period);
            status.RecordNextTick(language.Language, nextTick);
        }
    }

    /// <summary>
    /// Runs one tick. Any failure (e.g. a transient DB error while ranking the leaderboard) is
    /// logged, so the loop survives and retries on the next tick instead of stopping the host.
    /// </summary>
    /// <param name="nextDue">The language's next regular due time after this tick: the lease lasts until just before it.</param>
    internal async Task RunTickAsync(LanguageOptions language, bool startup, DateTime nextDue, CancellationToken stoppingToken)
    {
        // One trace per tick: the lease, the LLM calls, the SQL commands and the judge show up under it.
        using var activity = LazyDadTelemetry.ActivitySource.StartActivity("joke tick");
        activity?.SetTag("language", language.Language);
        activity?.SetTag("startup", startup);
        try
        {
            if (!await TakeTurnAsync(language, startup, nextDue, stoppingToken))
            {
                status.Record(new TickStatus(language.Language, DateTime.UtcNow, true, [], "skipped", null));
                metrics.RecordTick(language.Language, "skipped");
                logger.LogInformation("Skipped the '{Language}' tick: another replica generated this period, or it came too late.", language.Language);
                return;
            }

            var (saved, leaderboard) = await GenerateAndPersistAsync(language, stoppingToken);
            status.Record(new TickStatus(
                language.Language, DateTime.UtcNow, true,
                saved.Select(j => new GeneratedJoke(j.Id, j.Model)).ToList(), leaderboard, null));
            metrics.RecordTick(language.Language, "succeeded");
            metrics.RecordLeaderboard(language.Language, leaderboard);
            await ProfileJokesAsync(stoppingToken);
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
    private async Task<bool> TakeTurnAsync(LanguageOptions language, bool startup, DateTime nextDue, CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromHours(language.IntervalHours);
        // Ends a little early, so the holder's own next due time finds it expired. Every replica computes the same
        // due times (TickSchedule), so the others' ticks until then find it held and skip.
        var margin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(5).Ticks, period.Ticks / 10));
        var now = DateTime.UtcNow;
        var lockKey = $"jokes:{language.Language}";
        // A regular tick that runs late (say, after the host was suspended) keeps its slot only while at least half a
        // period of lease is left: far longer than a batch takes, so no other replica can take the slot over while
        // this one is still working. Later than that it skips the slot, since an expired (or soon expired) lease is
        // free to take and several replicas could generate the same batch. On time, a tick gets almost a period;
        // a startup tick's next due time is at least half a period away.
        if (!startup && nextDue - margin - now < period / 2)
        {
            logger.LogWarning("The '{Language}' tick came too late for its slot (the next one is due {NextDue:o}); skipping it.",
                language.Language, nextDue);
            return false;
        }

        using var scope = scopeFactory.CreateScope();
        var locks = scope.ServiceProvider.GetRequiredService<ISchedulerLockRepository>();
        if (startup)
        {
            await locks.AcquireAsync(lockKey, instanceId, now, nextDue - margin, stoppingToken);
            return true;
        }
        return await locks.TryAcquireAsync(lockKey, instanceId, now, nextDue - margin, stoppingToken);
    }

    private async Task<(IReadOnlyList<Joke> Saved, string Leaderboard)> GenerateAndPersistAsync(LanguageOptions language, CancellationToken stoppingToken)
    {
        // All models for the language are queried in parallel, and each joke is saved as soon as its model answers: a
        // slow model (a thinking one can take minutes) delays only its own joke, not the others'.
        var results = await Task.WhenAll(language.LlmModels.Select(async model =>
            await GenerateAsync(language, model, stoppingToken) is { } joke ? await SaveAsync(joke, stoppingToken) : null));
        // Duplicates go before the judge sees the new jokes: two models can write the same joke in one tick, and the judge
        // could put both copies in the Top 3, which the cleanup never deletes.
        var removed = await RemoveDuplicatesAsync(language.Language, stoppingToken);
        var saved = results.OfType<Joke>().Where(j => !removed.Contains(j.Id)).ToList();

        using var scope = scopeFactory.CreateScope();
        var topJokeService = scope.ServiceProvider.GetRequiredService<TopJokeService>();

        // Runs even when nothing new was saved, so an empty leaderboard still gets seeded.
        var leaderboard = "unchanged";
        try
        {
            if (await topJokeService.UpdateAsync(language.Language, saved, stoppingToken))
            {
                leaderboard = "updated";
                readCache.Invalidate();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            leaderboard = "failed";
            logger.LogError(ex, "Failed to update the top jokes for '{Language}'. Provider response: {ProviderResponse}",
                language.Language, ProviderResponse(ex));
        }

        return (saved, leaderboard);
    }

    /// <summary>
    /// Deletes later copies of the same joke (JokeRepository.RemoveDuplicatesAsync): the copies the old model saved weeks
    /// apart, and the rare one two models write in the same tick (each checks only what's saved before it). /status lists
    /// only jokes that still exist, so a copy deleted here (or by another replica) drops off it. Nothing here fails the tick.
    /// </summary>
    /// <returns>The ids it deleted (none if it failed, or another replica was cleaning).</returns>
    private async Task<IReadOnlyList<int>> RemoveDuplicatesAsync(string language, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            // One replica at a time (startup ticks overlap): two cleanups choosing from different snapshots could each
            // delete a different copy and leave none. Another replica cleaning now means this one has nothing to add.
            var locks = scope.ServiceProvider.GetRequiredService<ISchedulerLockRepository>();
            var lockKey = $"duplicates:{language}";
            var now = DateTime.UtcNow;
            if (!await locks.TryAcquireAsync(lockKey, instanceId, now, now + DuplicatesLeaseLength, stoppingToken))
                return [];
            try
            {
                var removed = await scope.ServiceProvider.GetRequiredService<IJokeRepository>().RemoveDuplicatesAsync(language, stoppingToken);
                if (removed.Count > 0)
                {
                    readCache.Invalidate();
                    logger.LogInformation("Removed {Count} duplicate '{Language}' joke(s).", removed.Count, language);
                }
                return removed;
            }
            finally
            {
                // Best-effort: a failure here mustn't lose the ids above (the judge must not see deleted jokes), and the
                // lease runs out on its own in DuplicatesLeaseLength.
                try
                {
                    await locks.ReleaseAsync(lockKey, instanceId, DateTime.UtcNow, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Couldn't release the '{LockKey}' lease; it runs out on its own.", lockKey);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Removing duplicate '{Language}' jokes failed; the next tick tries again.", language);
            return [];
        }
    }

    /// <summary>
    /// Profiles the jokes "you might also like" ranks by (JokeProfiler): this tick's jokes first, then a batch of older
    /// ones without a profile (the profiler takes its own lease, so two replicas never profile at once). Similar jokes
    /// are optional, so nothing here fails the tick: a failure waits for the next one.
    /// </summary>
    private async Task ProfileJokesAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IJokeProfiler>().ProfileAsync(stoppingToken) > 0)
                readCache.Invalidate();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Profiling jokes for similar jokes failed; the next tick tries again.");
        }
    }

    /// <summary>
    /// Saves one generated joke, in its own scope (the models' saves run in parallel, and a <c>DbContext</c> can't be
    /// shared), and reports it on <c>/status</c> right away. Returns <c>null</c> if saving failed.
    /// </summary>
    private async Task<Joke?> SaveAsync(Joke joke, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var jokeRepository = scope.ServiceProvider.GetRequiredService<IJokeRepository>();
        try
        {
            await jokeRepository.AddAsync(joke, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordJoke(joke.Language, joke.Model, "failed");
            logger.LogError(ex, "Failed to persist joke for '{Language}' ({Model}).", joke.Language, joke.Model);
            return null;
        }

        metrics.RecordJoke(joke.Language, joke.Model, "saved");
        status.RecordSavedJoke(new SavedJoke(joke.Language, joke.Id, joke.Model, DateTime.UtcNow));
        // This replica's visitors see it at once; the other region's when their cached copies expire.
        readCache.Invalidate();
        logger.LogInformation("Joke saved for '{Language}' ({Model}): {Text}", joke.Language, joke.Model, joke.Text);
        return joke;
    }

    /// <summary>
    /// Generates one joke with one model, trying up to <see cref="JokeGenerationOptions.Attempts"/> times when the model
    /// fails, answers empty, or repeats a joke the site already has (then at once, told which joke it repeated). Returns
    /// <c>null</c> once every try failed, so sibling models are unaffected; only that final outcome is counted (a joke saved
    /// on a later try counts as saved).
    /// </summary>
    private async Task<Joke?> GenerateAsync(LanguageOptions language, LlmModelOptions model, CancellationToken stoppingToken)
    {
        // Own scope per model: parallel calls must not share a DbContext.
        using var scope = scopeFactory.CreateScope();
        var generationService = scope.ServiceProvider.GetRequiredService<JokeGenerationService>();
        var jokeRepository = scope.ServiceProvider.GetRequiredService<IJokeRepository>();
        var attempts = options.Value.Attempts;
        var delay = TimeSpan.FromSeconds(options.Value.RetryDelaySeconds);
        // This model's answers that were jokes the site already has: the next try is told to write another.
        var repeated = new List<string>();
        // A try that failed or came back empty: if the last try then repeats a joke, that's what's counted (and alerted),
        // not "duplicate", which means every try repeated one.
        string? problem = null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                logger.LogInformation("Generating joke for '{Language}' using {Model} (try {Attempt} of {Attempts})...",
                    language.Language, model.Model, attempt, attempts);

                var draft = await generationService.GenerateAsync(language, model, repeated, stoppingToken);

                if (!string.IsNullOrWhiteSpace(draft.Text) && await jokeRepository.TextExistsAsync(language.Language, draft.Text, stoppingToken))
                {
                    repeated.Add(draft.Text);
                    if (attempt == attempts)
                    {
                        metrics.RecordJoke(language.Language, model.Model, problem ?? "duplicate");
                        logger.LogWarning("{Model} saved no '{Language}' joke: its last try repeated an existing one ({Text}); " +
                            "an earlier try: {Problem}.", model.Model, language.Language, draft.Text, problem ?? "duplicate");
                        return null;
                    }
                    // Not a failure to wait out: ask again at once, naming the joke it repeated.
                    logger.LogInformation("{Model} repeated an existing '{Language}' joke; asking for another: {Text}",
                        model.Model, language.Language, draft.Text);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(draft.Text))
                {
                    return new Joke
                    {
                        Language = language.Language,
                        Model = model.Model,
                        Text = draft.Text,
                        Explanation = draft.Explanation,
                        GeneratedAt = DateTime.UtcNow
                    };
                }

                if (attempt == attempts)
                {
                    metrics.RecordJoke(language.Language, model.Model, "empty");
                    logger.LogWarning("LLM returned an empty response for '{Language}' ({Model}) on every try. Skipping.",
                        language.Language, model.Model);
                    return null;
                }
                problem ??= "empty";
                logger.LogWarning("LLM returned an empty response for '{Language}' ({Model}); trying again in {Delay} s.",
                    language.Language, model.Model, delay.TotalSeconds);
            }
            // Only shutdown propagates. A provider timeout is also an OperationCanceledException;
            // rethrowing it would fault Task.WhenAll and discard the sibling models' jokes.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == attempts)
                {
                    metrics.RecordJoke(language.Language, model.Model, "failed");
                    logger.LogError(ex, "Failed to generate joke for '{Language}' ({Model}) on every try. Provider response: {ProviderResponse}",
                        language.Language, model.Model, ProviderResponse(ex));
                    return null;
                }
                problem = "failed";
                logger.LogWarning(ex, "Failed to generate joke for '{Language}' ({Model}); trying again in {Delay} s. Provider response: {ProviderResponse}",
                    language.Language, model.Model, delay.TotalSeconds, ProviderResponse(ex));
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>
    /// The body of a provider's error response, e.g. Azure's error code and message: the exception's own message
    /// ("Service request failed. Status: 400") leaves it out. It's about the request, whose content is jokes and
    /// prompts, never visitor data. "none" when the failure wasn't an error response (a timeout, a network error).
    /// </summary>
    private static string ProviderResponse(Exception exception)
    {
        if (exception is not ClientResultException clientError || clientError.GetRawResponse() is not { } response)
            return "none";
        try
        {
            var body = response.Content.ToString();
            return body.Length <= MaxLoggedResponseLength ? body : $"{body[..MaxLoggedResponseLength]}…";
        }
        catch (InvalidOperationException)
        {
            // The body wasn't buffered (a streamed response), so it can't be read again.
            return "unreadable";
        }
    }

    private const int MaxLoggedResponseLength = 2000;

    // Far longer than a cleanup takes (one read and a delete or two); it only runs out on its own if the process dies.
    private static readonly TimeSpan DuplicatesLeaseLength = TimeSpan.FromMinutes(5);
}
