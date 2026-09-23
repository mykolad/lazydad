using LazyDad.Api.Configuration;

namespace LazyDad.Tests;

public class JokeGenerationOptionsValidatorTests
{
    private static JokeGenerationOptions Options(int intervalHours, bool enabled)
        => new()
        {
            UniquenessSampleSize = 20,
            Languages =
            [
                new()
                {
                    Language = "Ukrainian",
                    Enabled = enabled,
                    IntervalHours = intervalHours,
                    LlmModels = [new() { Provider = "AzureOpenAI", Model = "gpt-5.3-chat" }]
                }
            ]
        };

    [Fact]
    public void Validate_ValidConfig_Succeeds()
        => Assert.True(new JokeGenerationOptionsValidator().Validate(null, Options(4, true)).Succeeded);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveIntervalOnEnabledLanguage_Fails(int intervalHours)
    {
        var result = new JokeGenerationOptionsValidator().Validate(null, Options(intervalHours, true));

        Assert.True(result.Failed);
        Assert.Contains("JokeGeneration:Languages:0:IntervalHours must be > 0", result.FailureMessage);
    }

    [Fact]
    public void Validate_NonPositiveIntervalOnDisabledLanguage_IsIgnored()
        => Assert.True(new JokeGenerationOptionsValidator().Validate(null, Options(0, false)).Succeeded);

    [Fact]
    public void Validate_ModelWithoutProvider_Fails()
    {
        var options = Options(4, true);
        options.Languages[0].LlmModels.Add(new() { Provider = "", Model = "gpt-6-luna" });

        Assert.True(new JokeGenerationOptionsValidator().Validate(null, options).Failed);
    }
}
