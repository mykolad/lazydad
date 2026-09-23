namespace LazyDad.Api.Configuration;

public class JokeGenerationOptions
{
    public const string SectionName = "JokeGeneration";

    public int UniquenessSampleSize { get; set; } = 20;
    public List<LanguageOptions> Languages { get; set; } = [];
}
