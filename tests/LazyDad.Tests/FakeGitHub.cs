using System.Net;
using System.Text;

namespace LazyDad.Tests;

/// <summary>
/// GitHub's back channel, as the GitHub handler calls it: the token exchange and the user endpoint. Plugged in as the
/// handler's BackchannelHttpHandler, so a sign-in completes without leaving the test.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public const string ClientId = "test-client-id";
    public const string ClientSecret = "test-client-secret";
    public const string AccessToken = "gho_test";

    /// <summary>What <c>https://api.github.com/user</c> answers.</summary>
    public string User { get; set; } = """{"id": 12345, "login": "alice", "name": "Alice Example", "email": "alice@example.com"}""";

    /// <summary>The token exchange's form, as sent.</summary>
    public Dictionary<string, string> TokenRequest { get; private set; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://github.com/login/oauth/access_token")
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            TokenRequest = form.Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
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
