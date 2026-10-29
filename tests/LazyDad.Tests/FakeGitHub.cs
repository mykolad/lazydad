using System.Net;
using System.Text;

namespace LazyDad.Tests;

/// <summary>
/// GitHub's back channel, as the GitHub handler and its probe call it: the token exchange and the user endpoint. Plugged
/// in as the primary handler of GitHub's back channel client (SignInBackchannel), so a sign-in completes without leaving
/// the test, and the app's own timing handler still runs.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string ClientSecret = "test-client-secret";
    public const string AccessToken = "gho_test";
    /// <summary>The only code it exchanges: what it would have sent the reader back with.</summary>
    public const string Code = "the-code";

    /// <summary>What <c>https://api.github.com/user</c> answers.</summary>
    public string User { get; set; } = """{"id": 12345, "login": "alice", "name": "Alice Example", "email": "alice@example.com"}""";

    /// <summary>The last successful token exchange's form, as sent.</summary>
    public Dictionary<string, string> TokenRequest { get; private set; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://github.com/login/oauth/access_token")
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            // As GitHub answers: 200, with the error in the body.
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret)
                return Json("""{"error": "incorrect_client_credentials"}""");
            if (form["code"] != Code)
                return Json("""{"error": "bad_verification_code"}""");
            TokenRequest = form;
            return Json($$"""{"access_token": "{{AccessToken}}", "token_type": "bearer", "scope": ""}""");
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == "https://api.github.com/user"
            && request.Headers.Authorization?.Parameter == AccessToken)
            return Json(User);

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
