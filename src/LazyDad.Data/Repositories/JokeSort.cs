namespace LazyDad.Data.Repositories;

public enum JokeSort
{
    /// <summary>Newest first.</summary>
    Newest,
    /// <summary>Highest net score (<c>Up - Down</c>) first, newest first among equal scores.</summary>
    TopVoted
}
