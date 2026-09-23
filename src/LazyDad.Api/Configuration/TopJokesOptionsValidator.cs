using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>
/// Validated at startup (ValidateOnStart). Without it, e.g. a non-positive Size would take
/// TopJokeService's trim path and replace the leaderboard with zero rows, and a missing judge
/// would only surface as a failure on the first tick.
/// </summary>
public class TopJokesOptionsValidator : IValidateOptions<TopJokesOptions>
{
    public ValidateOptionsResult Validate(string? name, TopJokesOptions options)
    {
        // A disabled leaderboard is never read, so its values don't matter.
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var errors = new List<string>();
        var section = TopJokesOptions.SectionName;

        if (options.Size < 1)
            errors.Add($"{section}:Size must be >= 1 (was {options.Size}).");

        if (options.SeedSampleSize < options.Size)
            errors.Add($"{section}:SeedSampleSize must be >= Size (was {options.SeedSampleSize}, Size {options.Size}).");

        if (string.IsNullOrWhiteSpace(options.Judge.Provider) || string.IsNullOrWhiteSpace(options.Judge.Model))
            errors.Add($"{section}:Judge needs both Provider and Model.");

        if (!Enum.IsDefined(options.JudgeReasoningEffort))
            errors.Add($"{section}:JudgeReasoningEffort '{options.JudgeReasoningEffort}' is not a known effort level.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
