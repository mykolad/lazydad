namespace LazyDad.Data.Entities;

/// <summary>
/// A joke's position for "you might also like", worked out once when the joke is saved (or by the backfill) and kept,
/// so similar jokes are ranked from stored numbers without calling anything. One row per joke and method.
/// </summary>
public class JokeProfile
{
    public const string JevKind = "jev";
    public const string EmbeddingKind = "embedding";

    public int JokeId { get; set; }
    public Joke Joke { get; set; } = null!;
    /// <summary>The method: <see cref="JevKind"/> (topic and wordplay probabilities) or <see cref="EmbeddingKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>
    /// What produced the vector (e.g. the pinned Jev model and the question set, or the embedding model and its size).
    /// Vectors of different versions aren't comparable, so a new version means profiling the jokes again.
    /// </summary>
    public string Version { get; set; } = string.Empty;
    /// <summary>The vector, as little-endian 32-bit floats.</summary>
    public byte[] Vector { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
