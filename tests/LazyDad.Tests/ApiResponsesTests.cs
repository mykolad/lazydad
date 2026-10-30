using System.Text.Json;
using LazyDad.Api.Controllers;
using LazyDad.Api.Services;
using LazyDad.Api.SignIn;

namespace LazyDad.Tests;

/// <summary>
/// The JSON of the responses Program.cs builds itself (<c>/healthz</c>, <c>/status</c>): Traffic Manager, Deploy Master's
/// smoke tests and the deploy's retry read these names. The controllers' responses are pinned in their own tests.
/// </summary>
public class ApiResponsesTests
{
    private static string Json(object response) => JsonSerializer.Serialize(response, JsonSerializerOptions.Web);

    [Fact]
    public void Health_KeepsItsJson()
    {
        Assert.Equal(
            """{"status":"healthy","version":"e33d99a","revision":"lazydad-app--abc"}""",
            Json(new HealthResponse("healthy", "e33d99a", "lazydad-app--abc")));
    }

    [Fact]
    public void Status_KeepsItsJson()
    {
        var at = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var status = new StatusResponse(
            "e33d99a",
            "lazydad-app--abc",
            "0123456789abcdef",
            [new TickStatus("Ukrainian", at, false, [new GeneratedJoke(7, "gpt-6-luna")], "failed", "HttpRequestException")],
            [new SavedJoke("Ukrainian", 7, "gpt-6-luna", at)],
            new SignInStatus(KeyRingState.Ok));

        // One line per field, joined: the JSON itself has no line breaks.
        var expected = """
            {"version":"e33d99a","revision":"lazydad-app--abc","process":"0123456789abcdef",
            "ticks":[{"language":"Ukrainian","completedAt":"2026-10-01T08:00:00Z","succeeded":false,"jokes":[{"id":7,"model":"gpt-6-luna"}],"leaderboard":"failed","error":"HttpRequestException"}],
            "savedJokes":[{"language":"Ukrainian","id":7,"model":"gpt-6-luna","savedAt":"2026-10-01T08:00:00Z"}],
            "signIn":{"keyRing":"ok"}}
            """.ReplaceLineEndings("");
        Assert.Equal(expected, Json(status));
    }
}
