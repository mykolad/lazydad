namespace LazyDad.Api.Configuration;

public class LlmProviderOptions
{
    public const string SectionName = "LlmProviders";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
}
