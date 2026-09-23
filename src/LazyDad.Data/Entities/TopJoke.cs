namespace LazyDad.Data.Entities;

/// <summary>A slot in a language's top-N leaderboard, as chosen by the judge LLM.</summary>
public class TopJoke
{
    public string Language { get; set; } = string.Empty;
    /// <summary>1-based; 1 is the best.</summary>
    public int Rank { get; set; }
    public int JokeId { get; set; }
    public Joke Joke { get; set; } = null!;
    /// <summary>The judge's short justification for this placement.</summary>
    public string Reason { get; set; } = string.Empty;
    public string JudgeModel { get; set; } = string.Empty;
    public DateTime SelectedAt { get; set; }
}
