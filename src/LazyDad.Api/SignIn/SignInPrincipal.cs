using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace LazyDad.Api.SignIn;

/// <summary>
/// The signed-in reader as the cookie carries them: the voter key and the provider's name, nothing else. Whatever a
/// provider sent (its account id, a name, an email) is dropped before the cookie is written (see SignInEvents).
/// </summary>
public static class SignInPrincipal
{
    public const string VoterClaim = "voter";
    public const string ProviderClaim = "provider";

    public static ClaimsPrincipal Create(string provider, byte[] voterKey)
        => new(new ClaimsIdentity(
            [new Claim(VoterClaim, Convert.ToBase64String(voterKey)), new Claim(ProviderClaim, provider)],
            CookieAuthenticationDefaults.AuthenticationScheme));

    public static bool TryRead(ClaimsPrincipal? principal, [NotNullWhen(true)] out byte[]? voterKey, [NotNullWhen(true)] out string? provider)
    {
        voterKey = null;
        provider = principal?.FindFirst(ProviderClaim)?.Value;
        var voter = principal?.FindFirst(VoterClaim)?.Value;
        if (principal?.Identity?.IsAuthenticated != true || provider is null || !SignInProviders.IsKnown(provider) || voter is null)
            return false;

        var key = new byte[voter.Length];
        if (!Convert.TryFromBase64String(voter, key, out var length) || length != Data.Entities.Vote.VoterKeyLength)
            return false;
        voterKey = key[..length];
        return true;
    }
}
