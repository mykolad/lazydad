namespace LazyDad.Data.Entities;

public class Joke
{
    public int Id { get; set; }
    public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
}
