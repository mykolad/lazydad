namespace LazyDad.Data.Entities;

public class Joke
{
    public int Id { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    /// <summary>Anonymous "funny" votes. The net score (<c>Up - Down</c>) can be negative.</summary>
    public int Up { get; set; }
    /// <summary>Anonymous "not funny" votes.</summary>
    public int Down { get; set; }
}
