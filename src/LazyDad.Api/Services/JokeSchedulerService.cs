using LazyDad.Api.Configuration;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class JokeSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<JokeGenerationOptions> options;
    private readonly SchedulerStatus status;
    private readonly ILogger<JokeSchedulerService> logger;

    public JokeSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<JokeGenerationOptions> options,
        SchedulerStatus status,
        ILogger<JokeSchedulerService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.status = status;
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
        await RunTickAsync(language, stoppingToken);

        var period = TimeSpan.FromHours(language.IntervalHours);
        using var timer = new PeriodicTimer(period);
        // The timer fires every period from its creation, so the due times are known in advance
        // (the page's countdown to the next batch).
        var nextTick = DateTime.UtcNow + period;
        status.RecordNextTick(language.Language, nextTick);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Advance before the tick runs; skip periods the timer coalesced if a tick overran.
            do nextTick += period; while (nextTick <= DateTime.UtcNow);
            status.RecordNextTick(language.Language, nextTick);
            await RunTickAsync(language, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one tick. Any failure (e.g. a transient DB error while ranking the leaderboard) is
    /// logged, so the loop survives and retries on the next tick instead of stopping the host.
    /// </summary>
    private async Task RunTickAsync(LanguageOptions language, CancellationToken stoppingToken)
    {
        try
        {
            var (saved, leaderboard) = await GenerateAndPersistAsync(language, stoppingToken);
            status.Record(new TickStatus(
                language.Language, DateTime.UtcNow, true,
                saved.Select(j => new GeneratedJoke(j.Id, j.Model)).ToList(), leaderboard, null));
            logger.LogDebug("Joke tick for '{Language}' completed.", language.Language);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            status.Record(new TickStatus(language.Language, DateTime.UtcNow, false, [], "unknown", ex.GetType().Name));
            logger.LogError(ex, "Joke tick for '{Language}' failed; will retry on the next tick.", language.Language);
        }
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

                logger.LogInformation("Joke saved for '{Language}' ({Model}): {Text}", joke.Language, joke.Model, joke.Text);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            logger.LogError(ex, "Failed to generate joke for '{Language}' ({Model}).", language.Language, model.Model);
            return null;
        }
    }
}
