namespace LazyDad.Api.Configuration;

public class LanguageOptions
{
    public string Language { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public string LlmProvider { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;
    public string PromptHint { get; set; } = string.Empty;
}
