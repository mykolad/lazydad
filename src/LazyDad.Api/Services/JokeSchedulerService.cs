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
        logger.LogInformation("Starting joke scheduler for language '{Language}' (every {Hours}h).",
            language.Language, language.IntervalHours);

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

        try
        {
            logger.LogInformation("Generating joke for '{Language}'...", language.Language);

            var text = await generationService.GenerateAsync(language, stoppingToken);

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("LLM returned an empty response for '{Language}'. Skipping.", language.Language);
                return;
            }

            var joke = new Joke
            {
                Language = language.Language,
                Text = text,
                GeneratedAt = DateTime.UtcNow
            };

            await jokeRepository.AddAsync(joke, stoppingToken);

            logger.LogInformation("Joke saved for '{Language}': {Text}", language.Language, text);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — exit cleanly without logging as an error.
            throw;
        }
        catch (Exception ex)
        {
            // Log and continue — one failure should not stop the scheduler.
            logger.LogError(ex, "Failed to generate or persist joke for '{Language}'.", language.Language);
        }
    }
}
