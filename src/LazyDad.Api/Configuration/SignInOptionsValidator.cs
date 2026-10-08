using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>
/// Validated at startup (ValidateOnStart), so a pepper that's too short or isn't base64 never makes a voter key, and
/// sign-in never runs with a key ring nobody but the database protects (except in Development).
/// </summary>
public class SignInOptionsValidator : IValidateOptions<SignInOptions>
{
    private readonly IOptions<KeyRingOptions> keyRing;
    private readonly IOptions<JokeGenerationOptions> jokeGeneration;
    private readonly IHostEnvironment environment;

    public SignInOptionsValidator(IOptions<KeyRingOptions> keyRing, IOptions<JokeGenerationOptions> jokeGeneration, IHostEnvironment environment)
    {
        this.keyRing = keyRing;
        this.jokeGeneration = jokeGeneration;
        this.environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, SignInOptions options)
    {
        // Checked even with sign-in off: a provider's lost setting must stop startup, not wait for the pepper.
        var errors = new List<string>();
        Client(errors, "GitHub", options.GitHub);
        Client(errors, "Google", options.Google);
        Client(errors, "Telegram", options.Telegram);
        Client(errors, "Facebook", options.Facebook);
        Microsoft(errors, options.Microsoft);
        Smoke(errors, options.Smoke);
        if (!options.Enabled)
            return Result(errors);

        var pepper = new byte[options.VoterKeyPepper.Length];
        if (!Convert.TryFromBase64String(options.VoterKeyPepper, pepper, out var length) || length < SignInOptions.MinPepperBytes)
            errors.Add($"{SignInOptions.SectionName}:VoterKeyPepper must be base64 of at least {SignInOptions.MinPepperBytes} random bytes.");

        var keyId = keyRing.Value.KeyVaultKeyId;
        if (keyId.Length > 0 && !keyRing.Value.TryGetKeyId(out _))
            errors.Add($"{KeyRingOptions.SectionName}:KeyVaultKeyId must be a Key Vault key's https URL without a version (…/keys/<name>), was '{keyId}'.");
        else if (keyId.Length == 0 && !environment.IsDevelopment() && !(options.LoadTest.Enabled && environment.IsEnvironment(LoadTestEnvironment)))
            errors.Add($"Sign-in is on, so {KeyRingOptions.SectionName}:KeyVaultKeyId must name the Key Vault key that protects " +
                "the cookies' key ring (only Development and a load test may leave it unprotected).");

        LoadTest(errors, options);
        return Result(errors);
    }

    /// <summary>The environment only the Load Test Environment workflow runs the app in (ASPNETCORE_ENVIRONMENT).</summary>
    public const string LoadTestEnvironment = "LoadTest";

    // The fake identity provider signs anyone in: an app trusting it must be the load-test app. Settings alone can't
    // prove that (a production app with its scheduler turned off would pass), so it also takes the environment only the
    // load-test workflow sets; and the app never generates jokes nor is smoke-tested there.
    private void LoadTest(List<string> errors, SignInOptions options)
    {
        if (!options.LoadTest.Enabled)
            return;
        var section = $"{SignInOptions.SectionName}:LoadTest:Authority";
        if (!environment.IsEnvironment(LoadTestEnvironment))
            errors.Add($"{section} is only for the load test: the app must run in the {LoadTestEnvironment} environment, not {environment.EnvironmentName}.");
        if (!Uri.TryCreate(options.LoadTest.Authority, UriKind.Absolute, out var authority) || authority.Scheme is not ("https" or "http"))
            errors.Add($"{section} must be the fake identity provider's address.");
        if (jokeGeneration.Value.Languages.Any(language => language.Enabled))
            errors.Add($"{section} is only for the load test: turn the scheduler off (every JokeGeneration language disabled).");
        if (options.Smoke.Enabled)
            errors.Add($"{section} is only for the load test: turn the smoke tests' sign-in off ({SignInOptions.SectionName}:Smoke).");
    }

    private static ValidateOptionsResult Result(List<string> errors)
        => errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

    // A client id without its secret (or the other way round) is a setting that went missing.
    private static void Client(List<string> errors, string provider, OAuthClientOptions client)
    {
        if (client.ClientId.Length > 0 != client.ClientSecret.Length > 0)
            errors.Add($"{SignInOptions.SectionName}:{provider} needs both ClientId and ClientSecret, or neither.");
    }

    // The audience, its tenant, and at least one identity to trust, all of them ids; nothing at all is off. Trusting
    // everyone in the tenant by accident (no ids) must stop startup.
    private static void Smoke(List<string> errors, SmokeSignInOptions smoke)
    {
        var section = $"{SignInOptions.SectionName}:Smoke";
        if (!smoke.Enabled)
        {
            if (smoke.TenantId.Length > 0 || smoke.AllowedObjectIds.Count > 0)
                errors.Add($"{section} has settings but no Audience.");
            return;
        }
        if (!Guid.TryParseExact(smoke.TenantId, "D", out _))
            errors.Add($"{section}:TenantId must be the tenant id (a GUID).");
        if (smoke.AllowedObjectIds.Count == 0 || !smoke.AllowedObjectIds.All(id => Guid.TryParseExact(id, "D", out _)))
            errors.Add($"{section}:AllowedObjectIds must list the deploy identity's object id (GUIDs), at least one.");
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
