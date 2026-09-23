using LazyDad.Api.Configuration;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class JokeSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<JokeGenerationOptions> options;
    private readonly ILogger<JokeSchedulerService> logger;

    public JokeSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptions<JokeGenerationOptions> options,
        ILogger<JokeSchedulerService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
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
        await GenerateAndPersistAsync(language, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(language.IntervalHours));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await GenerateAndPersistAsync(language, stoppingToken);
        }
    }

    private async Task GenerateAndPersistAsync(LanguageOptions language, CancellationToken stoppingToken)
    {
        // All models for the language are queried in parallel.
        var generated = await Task.WhenAll(language.LlmModels.Select(model => GenerateAsync(language, model, stoppingToken)));

        using var scope = scopeFactory.CreateScope();
        var jokeRepository = scope.ServiceProvider.GetRequiredService<IJokeRepository>();
        var topJokeService = scope.ServiceProvider.GetRequiredService<TopJokeService>();
        var htmlGenerator = scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>();

        var saved = new List<Joke>();

        foreach (var joke in generated.OfType<Joke>())
        {
            try
            {
                await jokeRepository.AddAsync(joke, stoppingToken);
                saved.Add(joke);

                logger.LogInformation("Joke saved for '{Language}' ({Model}): {Text}", joke.Language, joke.Model, joke.Text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist joke for '{Language}' ({Model}).", joke.Language, joke.Model);
            }
        }

        // Runs even when nothing new was saved, so an empty leaderboard still gets seeded.
        var topChanged = false;
        try
        {
            topChanged = await topJokeService.UpdateAsync(language.Language, saved, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update the top jokes for '{Language}'.", language.Language);
        }

        if (saved.Count > 0 || topChanged)
            await htmlGenerator.RegenerateAsync(stoppingToken);
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
        catch (OperationCanceledException)
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
