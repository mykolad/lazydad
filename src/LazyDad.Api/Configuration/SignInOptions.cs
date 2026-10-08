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

    /// <summary>Sign in with GitHub: an OAuth app per environment. Off while its client id is empty.</summary>
    public OAuthClientOptions GitHub { get; set; } = new();

    /// <summary>Sign in with Google: an OAuth client per environment. Off while its client id is empty.</summary>
    public OAuthClientOptions Google { get; set; } = new();

    /// <summary>Sign in with Telegram: a bot per environment (@BotFather's Login Widget settings). Off while its client id is empty.</summary>
    public OAuthClientOptions Telegram { get; set; } = new();

    /// <summary>
    /// Sign in with Facebook: a Meta app per environment (its App ID and App Secret). Off while its client id is empty.
    /// The secret also checks Meta's data-deletion requests.
    /// </summary>
    public OAuthClientOptions Facebook { get; set; } = new();

    /// <summary>A load test's fake identity provider (ProviderEndpoints). Never on staging or production.</summary>
    public LoadTestSignInOptions LoadTest { get; set; } = new();

    /// <summary>The smoke tests' sign-in as the deploy identity (SmokeSignIn). Off while its audience is empty.</summary>
    public SmokeSignInOptions Smoke { get; set; } = new();

    /// <summary>Sign in with Microsoft: an Entra app registration per environment. Off while its client id is empty.</summary>
    public MicrosoftClientOptions Microsoft { get; set; } = new();

    // Only empty means off: a value of spaces is a mistake, so it goes to the validator and fails startup.
    public bool Enabled => !string.IsNullOrEmpty(VoterKeyPepper);
}

/// <summary>
/// An OAuth app's registration at a provider. The secret is a Key Vault reference in Azure (e.g.
/// <c>GitHubClientSecret</c>, <c>GitHubClientSecretStaging</c>).
/// </summary>
public class OAuthClientOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    // The validator fails startup when only one of them is set: that's a lost setting, not "off".
    public bool Configured => ClientId.Length > 0;
}

/// <summary>
/// The Entra app registration. It proves itself with either a secret (<c>MicrosoftClientSecret</c> in Key Vault, which
/// expires and needs rotating) or, with no secret at all, a user-assigned managed identity the registration trusts as a
/// federated credential (<see cref="ManagedIdentityClientId"/>; Entra accepts only user-assigned ones).
/// </summary>
public class MicrosoftClientOptions : OAuthClientOptions
{
    /// <summary>The registration's own tenant: the probe asks it for a token as the app (the common endpoint can't tell).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The client id of the user-assigned managed identity whose token stands in for the secret.</summary>
    public string ManagedIdentityClientId { get; set; } = string.Empty;

    public bool UsesManagedIdentity => ManagedIdentityClientId.Length > 0;
}

/// <summary>
/// Who the smoke tests' token may come from: Entra tokens of this tenant, for <see cref="Audience"/>, whose <c>oid</c> is
/// one of <see cref="AllowedObjectIds"/> (the environment's deploy identity, lazydad-github-cd or lazydad-github-staging).
/// </summary>
public class SmokeSignInOptions
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The app registration the token is for: <c>api://lazydad-smoke</c>, an audience with no secret.</summary>
    public string Audience { get; set; } = string.Empty;

    public List<string> AllowedObjectIds { get; set; } = [];

    public bool Enabled => Audience.Length > 0;
}

/// <summary>
/// Points every configured provider at the load test's fake identity provider (<c>tests/load/FakeIdentityProvider.cs</c>),
/// which signs anyone in as a made-up account. Only for the load-test app: the validator refuses it while the scheduler or
/// the smoke sign-in is on, and Deploy Environment refuses to deploy an app that has it.
/// </summary>
public class LoadTestSignInOptions
{
    /// <summary>The fake's address, e.g. <c>https://lazydad-idp-loadtest.&lt;environment domain&gt;</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    public bool Enabled => Authority.Length > 0;
}
