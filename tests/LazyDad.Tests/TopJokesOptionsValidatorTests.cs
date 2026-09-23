using LazyDad.Api.Configuration;
using Microsoft.Extensions.AI;

namespace LazyDad.Tests;

public class TopJokesOptionsValidatorTests
{
    private static TopJokesOptions Valid()
        => new()
        {
            Enabled = true,
            Size = 3,
            SeedSampleSize = 100,
            Judge = new() { Provider = "AzureOpenAI", Model = "gpt-6-sol" },
            JudgeReasoningEffort = ReasoningEffort.High
        };

    private static string? Validate(TopJokesOptions options)
        => new TopJokesOptionsValidator().Validate(null, options).FailureMessage;

    [Fact]
    public void Validate_ValidConfig_Succeeds()
        => Assert.True(new TopJokesOptionsValidator().Validate(null, Valid()).Succeeded);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSize_Fails(int size)
    {
        var options = Valid();
        options.Size = size;

        Assert.Contains("TopJokes:Size must be >= 1", Validate(options));
    }

    [Fact]
    public void Validate_SeedSampleSmallerThanSize_Fails()
    {
        var options = Valid();
        options.SeedSampleSize = 2;

        Assert.Contains("SeedSampleSize must be >= Size", Validate(options));
    }

    [Theory]
    [InlineData("", "gpt-6-sol")]
    [InlineData("AzureOpenAI", "")]
    public void Validate_IncompleteJudge_Fails(string provider, string model)
    {
        var options = Valid();
        options.Judge = new() { Provider = provider, Model = model };

        Assert.Contains("Judge needs both Provider and Model", Validate(options));
    }

    [Fact]
    public void Validate_UnknownReasoningEffort_Fails()
    {
        var options = Valid();
        options.JudgeReasoningEffort = (ReasoningEffort)42;

        Assert.Contains("JudgeReasoningEffort", Validate(options));
    }

    [Fact]
    public void Validate_Disabled_IgnoresOtherValues()
    {
        var options = new TopJokesOptions { Enabled = false, Size = -5, Judge = new() };

        Assert.True(new TopJokesOptionsValidator().Validate(null, options).Succeeded);
    }
}
