using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Meta's <c>signed_request</c>: <c>&lt;signature&gt;.&lt;payload&gt;</c>, both base64url, the signature HMAC-SHA256 of the
/// payload's text keyed with the app secret
/// (https://developers.facebook.com/docs/development/create-an-app/app-dashboard/data-deletion-callback).
/// </summary>
public static class FacebookSignedRequest
{
    /// <summary>The app-scoped user id a request signed with <paramref name="appSecret"/> names; false for anything else.</summary>
    public static bool TryReadUserId(string? signedRequest, string appSecret, out string userId)
    {
        userId = string.Empty;
        var parts = signedRequest?.Split('.') ?? [];
        if (parts.Length != 2 || appSecret.Length == 0)
            return false;
        try
        {
            var signature = WebEncoders.Base64UrlDecode(parts[0]);
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.ASCII.GetBytes(parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(signature, expected))
                return false;

            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("algorithm", out var algorithm) || !string.Equals(algorithm.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("user_id", out var user) || user.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(user.GetString()))
                return false;
            userId = user.GetString()!;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
