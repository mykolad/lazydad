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
        using var scope = scopeFactory.CreateScope();
        var generationService = scope.ServiceProvider.GetRequiredService<JokeGenerationService>();
        var jokeRepository = scope.ServiceProvider.GetRequiredService<IJokeRepository>();
        var htmlGenerator = scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>();

        var anySucceeded = false;

        foreach (var model in language.LlmModels)
        {
            try
            {
                logger.LogInformation("Generating joke for '{Language}' using {Model}...", language.Language, model.Model);

                var text = await generationService.GenerateAsync(language, model, stoppingToken);

                if (string.IsNullOrWhiteSpace(text))
                {
                    logger.LogWarning("LLM returned an empty response for '{Language}' ({Model}). Skipping.", language.Language, model.Model);
                    continue;
                }

                var joke = new Joke
                {
                    Language = language.Language,
                    Model = model.Model,
                    Text = text,
                    GeneratedAt = DateTime.UtcNow
                };

                await jokeRepository.AddAsync(joke, stoppingToken);

                logger.LogInformation("Joke saved for '{Language}' ({Model}): {Text}", language.Language, model.Model, text);

                anySucceeded = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to generate or persist joke for '{Language}' ({Model}).", language.Language, model.Model);
            }
        }

        if (anySucceeded)
            await htmlGenerator.RegenerateAsync(stoppingToken);
    }
}
