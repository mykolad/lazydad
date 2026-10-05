namespace LazyDad.Api.Configuration;

/// <summary>
/// Where the sign-in cookies' key ring is protected. The ring itself is kept in the database (<c>DataProtectionKeys</c>),
/// so both production apps share it; each key in it is encrypted with this Key Vault key, so a database copy alone
/// can't open a cookie.
/// </summary>
public class KeyRingOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// The Key Vault key, without a version (<c>https://lazydad-kv.vault.azure.net/keys/DataProtection</c>, or
    /// <c>…/DataProtectionStaging</c>), so a rotated key needs no new setting. Required wherever sign-in is on, except
    /// in Development.
    /// </summary>
    public string KeyVaultKeyId { get; set; } = string.Empty;

    /// <summary>An https URL whose path is exactly <c>/keys/&lt;name&gt;</c>: no version, so it follows the key's rotation.</summary>
    public bool TryGetKeyId(out Uri keyId)
        => Uri.TryCreate(KeyVaultKeyId, UriKind.Absolute, out keyId!)
            && keyId.Scheme == Uri.UriSchemeHttps
            && keyId.Query.Length == 0 && keyId.Fragment.Length == 0
            && keyId.AbsolutePath.Split('/') is ["", "keys", { Length: > 0 }];
}
