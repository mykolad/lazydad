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
}
