namespace LazyDad.Api.Configuration;

public class JokeGenerationOptions
{
    public const string SectionName = "JokeGeneration";

    public int UniquenessSampleSize { get; set; } = 20;

    /// <summary>
    /// Tries per model and tick: a model that fails (an error, a timeout) or answers empty is asked again, so a
    /// transient problem doesn't cost that model's joke until the next tick, 4 hours later.
    /// </summary>
    public int Attempts { get; set; } = 3;

    /// <summary>Seconds between a model's tries.</summary>
    public int RetryDelaySeconds { get; set; } = 30;

    public List<LanguageOptions> Languages { get; set; } = [];
}
