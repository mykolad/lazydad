using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>
/// Validated at startup (ValidateOnStart), so a pepper that's too short or isn't base64 never makes a voter key, and
/// sign-in never runs with a key ring nobody but the database protects (except in Development).
/// </summary>
public class SignInOptionsValidator : IValidateOptions<SignInOptions>
{
    private readonly IOptions<KeyRingOptions> keyRing;
    private readonly IHostEnvironment environment;

    public SignInOptionsValidator(IOptions<KeyRingOptions> keyRing, IHostEnvironment environment)
    {
        this.keyRing = keyRing;
        this.environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, SignInOptions options)
    {
        // Checked even with sign-in off: a provider's lost setting must stop startup, not wait for the pepper.
        var errors = new List<string>();
        Client(errors, "GitHub", options.GitHub);
        Client(errors, "Google", options.Google);
        Microsoft(errors, options.Microsoft);
        if (!options.Enabled)
            return Result(errors);

        var pepper = new byte[options.VoterKeyPepper.Length];
        if (!Convert.TryFromBase64String(options.VoterKeyPepper, pepper, out var length) || length < SignInOptions.MinPepperBytes)
            errors.Add($"{SignInOptions.SectionName}:VoterKeyPepper must be base64 of at least {SignInOptions.MinPepperBytes} random bytes.");

        var keyId = keyRing.Value.KeyVaultKeyId;
        if (keyId.Length > 0 && !keyRing.Value.TryGetKeyId(out _))
            errors.Add($"{KeyRingOptions.SectionName}:KeyVaultKeyId must be a Key Vault key's https URL without a version (…/keys/<name>), was '{keyId}'.");
        else if (keyId.Length == 0 && !environment.IsDevelopment())
            errors.Add($"Sign-in is on, so {KeyRingOptions.SectionName}:KeyVaultKeyId must name the Key Vault key that protects " +
                "the cookies' key ring (only Development may leave it unprotected).");

        return Result(errors);
    }

    private static ValidateOptionsResult Result(List<string> errors)
        => errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

    // A client id without its secret (or the other way round) is a setting that went missing.
    private static void Client(List<string> errors, string provider, OAuthClientOptions client)
    {
        if (client.ClientId.Length > 0 != client.ClientSecret.Length > 0)
            errors.Add($"{SignInOptions.SectionName}:{provider} needs both ClientId and ClientSecret, or neither.");
    }

    // A client id, its tenant, and exactly one way to prove itself: a secret or a managed identity. Nothing at all is off.
    private static void Microsoft(List<string> errors, MicrosoftClientOptions microsoft)
    {
        var section = $"{SignInOptions.SectionName}:Microsoft";
        if (!microsoft.Configured)
        {
            if (microsoft.ClientSecret.Length > 0 || microsoft.TenantId.Length > 0 || microsoft.UsesManagedIdentity)
                errors.Add($"{section} has settings but no ClientId.");
            return;
        }
        // Entra ids are GUIDs: a mangled one should stop startup, not the first sign-in.
        if (!Guid.TryParseExact(microsoft.ClientId, "D", out _))
            errors.Add($"{section}:ClientId must be the app registration's client id (a GUID).");
        if (!Guid.TryParseExact(microsoft.TenantId, "D", out _))
            errors.Add($"{section}:TenantId must be the app registration's tenant id (a GUID).");
        if (microsoft.UsesManagedIdentity && !Guid.TryParseExact(microsoft.ManagedIdentityClientId, "D", out _))
            errors.Add($"{section}:ManagedIdentityClientId must be the managed identity's client id (a GUID).");
        if (microsoft.ClientSecret.Length > 0 == microsoft.UsesManagedIdentity)
            errors.Add($"{section} needs either ClientSecret or ManagedIdentityClientId (a federated credential), not both.");
    }
}
