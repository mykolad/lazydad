namespace LazyDad.Data.Entities;

/// <summary>
/// A signed-in reader's vote on a joke. The reader is only a voter key: a keyed hash of the sign-in provider and the
/// provider's id for the account (see the API's VoterKeys), so no account id, name or address is stored. One row per
/// joke and voter; removing a vote deletes the row.
/// </summary>
public class Vote
{
    /// <summary>An HMAC-SHA256.</summary>
    public const int VoterKeyLength = 32;

    public int JokeId { get; set; }
    public Joke Joke { get; set; } = null!;
    public byte[] VoterKey { get; set; } = [];
    /// <summary>1 ("funny") or -1 ("not funny").</summary>
    public short Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}
