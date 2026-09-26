namespace LazyDad.Data.Repositories;

public enum JokeSort
{
    /// <summary>Newest first.</summary>
    Newest,
    /// <summary>
    /// Highest net score (<c>Up - Down</c>) first, newest first among equal scores. A joke whose score
    /// changes while a reader scrolls can still move across the page they're at (no snapshot).
    /// </summary>
    TopVoted
}
