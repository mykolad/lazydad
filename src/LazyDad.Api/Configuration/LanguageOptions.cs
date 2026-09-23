namespace LazyDad.Api.Configuration;

public class LanguageOptions
{
    public string Language { get; set; } = string.Empty;
    /// <summary>BCP 47 code (e.g. "uk") used for the jokes' HTML lang attribute.</summary>
    public string LanguageCode { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public string PromptHint { get; set; } = string.Empty;
    public List<LlmModelOptions> LlmModels { get; set; } = [];
}
