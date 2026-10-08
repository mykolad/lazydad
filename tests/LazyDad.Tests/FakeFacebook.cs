using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.WebUtilities;

namespace LazyDad.Tests;

/// <summary>
/// Facebook's back channel, as the Facebook handler and its probe call it: the Graph API's token endpoint (a code, or the
/// app's own token) and <c>/me</c>, with Meta's own error objects.
/// </summary>
public sealed class FakeFacebook : HttpMessageHandler
{
    public const string AppId = "1234567890";
    public const string AppSecret = "test-app-secret";
    public const string Code = "the-code";
    public const string UserId = "10229876543210987";
    private const string AccessToken = "test-user-token";

    /// <summary>The fields the last <c>/me</c> asked for.</summary>
    public string? Fields { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (request.Method == HttpMethod.Post && path == FacebookDefaults.TokenEndpoint)
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
            if (form.GetValueOrDefault("client_id") != AppId || form.GetValueOrDefault("client_secret") != AppSecret)
                return Json(HttpStatusCode.BadRequest, """{"error": {"message": "Error validating client secret.", "type": "OAuthException", "code": 1}}""");
            if (form.GetValueOrDefault("grant_type") == "client_credentials")
                return Json(HttpStatusCode.OK, $$"""{"access_token": "{{AppId}}|app-token", "token_type": "bearer"}""");
            if (form.GetValueOrDefault("code") != Code)
                return Json(HttpStatusCode.BadRequest, """{"error": {"message": "Invalid verification code format.", "type": "OAuthException", "code": 100}}""");
            return Json(HttpStatusCode.OK, $$"""{"access_token": "{{AccessToken}}", "token_type": "bearer", "expires_in": 5183944}""");
        }

        if (request.Method == HttpMethod.Get && path == FacebookDefaults.UserInformationEndpoint)
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (query["access_token"] != AccessToken)
                return Json(HttpStatusCode.BadRequest, """{"error": {"message": "Invalid OAuth access token.", "type": "OAuthException", "code": 190}}""");
            Fields = query["fields"];
            // Whatever it's asked for, as if the reader's profile held more.
            return Json(HttpStatusCode.OK, $$"""{"id": "{{UserId}}", "name": "Alice Example", "email": "alice@example.com"}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
