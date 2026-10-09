using LazyDad.Api.Services;
using LazyDad.Api.SignIn;
using LazyDad.Data.Entities;

namespace LazyDad.Api.Controllers;

// What the API answers, apart from the entities: a column added to Joke isn't served until it's added here, and
// renaming one doesn't change the JSON that app.js reads.

public sealed record JokeResponse(
    int Id, string Language, string Model, string Text, DateTime GeneratedAt, string? Explanation, int Up, int Down)
{
    public static JokeResponse From(Joke joke)
        => new(joke.Id, joke.Language, joke.Model, joke.Text, joke.GeneratedAt, joke.Explanation, joke.Up, joke.Down);
}

public sealed record TopJokeResponse(string Language, int Rank, string Reason, string JudgeModel, DateTime SelectedAt, JokeResponse Joke)
{
    public static TopJokeResponse From(TopJoke top)
        => new(top.Language, top.Rank, top.Reason, top.JudgeModel, top.SelectedAt, JokeResponse.From(top.Joke));
}

/// <param name="Next">The cursor for the next page, or null on the last one.</param>
public sealed record FeedPage(int Total, IReadOnlyList<JokeResponse> Items, string? Next);

/// <param name="NextBatchAt">When this revision's scheduler runs next (UTC), or null before it's scheduled.</param>
public sealed record JokeSummary(int Count, DateTime? NextBatchAt);

public sealed record VoteCounts(int Up, int Down);

public sealed record MeResponse(bool SignedIn, string? Provider);

public sealed record HealthResponse(string Status, string Version, string Revision);

public sealed record StatusResponse(
    string Version,
    string Revision,
    string Process,
    IReadOnlyList<TickStatus> Ticks,
    IReadOnlyList<SavedJoke> SavedJokes,
    SignInStatus SignIn);

public sealed record SignInStatus(KeyRingState KeyRing);
