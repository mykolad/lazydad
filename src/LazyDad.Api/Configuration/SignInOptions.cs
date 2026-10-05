namespace LazyDad.Api.Configuration;

/// <summary>Sign-in, which voting needs (see VoterKeys). Off while <see cref="VoterKeyPepper"/> is empty.</summary>
public class SignInOptions
{
    public const string SectionName = "SignIn";
    public const int MinPepperBytes = 32;

    /// <summary>
    /// The secret that turns a provider's account id into a voter key: base64, at least 32 random bytes
    /// (<c>openssl rand -base64 32</c>). A Key Vault reference in Azure (<c>VoterKeyPepper</c>,
    /// <c>VoterKeyPepperStaging</c>). It can't be changed or recreated: with a new one every voter is new, and their
    /// earlier votes stay counted but can no longer be changed or deleted.
    /// </summary>
    public string VoterKeyPepper { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(VoterKeyPepper);
}
