using Microsoft.Extensions.AI;

namespace LazyDad.Api.Configuration;

public class TopJokesOptions
{
    public const string SectionName = "TopJokes";

    public bool Enabled { get; set; }
    /// <summary>How many jokes each language's leaderboard holds.</summary>
    public int Size { get; set; } = 3;
    /// <summary>How many recent jokes the judge picks from when the leaderboard is empty or short.</summary>
    public int SeedSampleSize { get; set; } = 100;
    public LlmModelOptions Judge { get; set; } = new();
    public ReasoningEffort JudgeReasoningEffort { get; set; } = ReasoningEffort.High;
}
