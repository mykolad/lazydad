using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>
/// Validated at startup (ValidateOnStart). Without it, e.g. a BatchSize of 0 would quietly stop all profiling, and a bad
/// endpoint or embedding size would only show as a failure at every tick. A method that's off (no Jev key, no embedding
/// deployment) isn't checked: its other settings are never read.
/// </summary>
public class SimilarityOptionsValidator : IValidateOptions<SimilarityOptions>
{
    // text-embedding-3-small has 1536 dimensions, -large 3072; a profile must also fit the table's varbinary(8000).
    public const int MaxDimensions = 2000;
    public const int MaxBatchSize = 1000;

    public ValidateOptionsResult Validate(string? name, SimilarityOptions options)
    {
        var errors = new List<string>();
        var section = SimilarityOptions.SectionName;

        if (options.BatchSize is < 1 or > MaxBatchSize)
            errors.Add($"{section}:BatchSize must be between 1 and {MaxBatchSize} (was {options.BatchSize}).");

        if (options.Jev.Enabled)
        {
            if (!Uri.TryCreate(options.Jev.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
                errors.Add($"{section}:Jev:Endpoint must be an absolute https URL (was '{options.Jev.Endpoint}').");
            if (string.IsNullOrWhiteSpace(options.Jev.Model))
                errors.Add($"{section}:Jev:Model must name a pinned model version (e.g. jev-1.13.0).");
        }

        if (options.Embeddings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.Embeddings.Provider))
                errors.Add($"{section}:Embeddings:Provider must name one of LlmProviders.");
            if (options.Embeddings.Dimensions is < 1 or > MaxDimensions)
                errors.Add($"{section}:Embeddings:Dimensions must be between 1 and {MaxDimensions} (was {options.Embeddings.Dimensions}).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
