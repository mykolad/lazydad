using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>
/// Validated at startup (ValidateOnStart), so a bad config fails the deploy with a clear
/// message instead of faulting a scheduler loop later (e.g. PeriodicTimer throws on a
/// non-positive interval).
/// </summary>
public class JokeGenerationOptionsValidator : IValidateOptions<JokeGenerationOptions>
{
    public ValidateOptionsResult Validate(string? name, JokeGenerationOptions options)
    {
        var errors = new List<string>();

        if (options.UniquenessSampleSize < 0)
            errors.Add($"{JokeGenerationOptions.SectionName}:UniquenessSampleSize must be >= 0.");

        for (var i = 0; i < options.Languages.Count; i++)
        {
            var language = options.Languages[i];
            var path = $"{JokeGenerationOptions.SectionName}:Languages:{i}";

            if (string.IsNullOrWhiteSpace(language.Language))
                errors.Add($"{path}:Language is required.");

            if (!language.Enabled)
                continue;

            if (language.IntervalHours <= 0)
                errors.Add($"{path}:IntervalHours must be > 0 (was {language.IntervalHours}).");

            if (language.LlmModels.Any(m => string.IsNullOrWhiteSpace(m.Provider) || string.IsNullOrWhiteSpace(m.Model)))
                errors.Add($"{path}:LlmModels entries need both Provider and Model.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
