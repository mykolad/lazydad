using System.Security.Cryptography;
using System.Text;
using LazyDad.Api.Configuration;
using LazyDad.Data.Entities;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Who a vote belongs to, without storing who that is: HMAC-SHA256 of the provider and the provider's id for the
/// account, keyed with the pepper (<c>SignIn:VoterKeyPepper</c>, kept in Key Vault). The same account always gets the
/// same key, so a vote can be found and changed. A plain hash wouldn't do: GitHub's account ids are public numbers,
/// so anyone could hash them all and match the keys; without the pepper they can't. The account id itself is never
/// stored or logged.
/// </summary>
public class VoterKeys
{
    private readonly byte[]? pepper;

    public VoterKeys(IOptions<SignInOptions> options)
    {
        pepper = options.Value.Enabled ? Convert.FromBase64String(options.Value.VoterKeyPepper) : null;
    }

    public bool Enabled => pepper is not null;

    /// <summary>The voter key for a provider's account (<see cref="Vote.VoterKeyLength"/> bytes).</summary>
    public byte[] For(string provider, string accountId)
    {
        if (pepper is null)
            throw new InvalidOperationException($"Sign-in is off: {SignInOptions.SectionName}:VoterKeyPepper is empty.");
        if (!SignInProviders.IsKnown(provider) && provider != SignInProviders.Smoke)
            throw new ArgumentException($"'{provider}' isn't a sign-in provider.", nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        // Provider names never contain ':', so "<provider>:<id>" can't be read two ways.
        return HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes($"{provider}:{accountId}"));
    }
}
