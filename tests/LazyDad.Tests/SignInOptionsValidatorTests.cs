using LazyDad.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class SignInOptionsValidatorTests
{
    private const string KeyId = "https://lazydad-kv.vault.azure.net/keys/DataProtection";
    private static readonly string Pepper = Convert.ToBase64String(new byte[SignInOptions.MinPepperBytes]);

    private static ValidateOptionsResult Validate(SignInOptions options, string keyId, string environment)
        => new SignInOptionsValidator(
                Options.Create(new KeyRingOptions { KeyVaultKeyId = keyId }),
                Options.Create(new JokeGenerationOptions()),
                Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment))
            .Validate(null, options);

    private static ValidateOptionsResult Validate(string pepper, string keyId, string environment)
        => Validate(new SignInOptions { VoterKeyPepper = pepper }, keyId, environment);

    private static ValidateOptionsResult Validate(string pepper) => Validate(pepper, KeyId, Environments.Production);

    [Fact]
    public void Validate_WithoutAPepper_Passes() => Assert.True(Validate("", "", Environments.Production).Succeeded);

    [Fact]
    public void Validate_ThirtyTwoBytesAndAKey_Pass() => Assert.True(Validate(Pepper).Succeeded);

    [Theory]
    [InlineData(" ")]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void Validate_RejectsAPepperThatIsBlankNotBase64OrTooShort(string pepper)
        => Assert.Contains(Validate(pepper).Failures!, f => f.Contains("VoterKeyPepper"));

    [Fact]
    public void Validate_WithSignInOn_NeedsTheKeyVaultKey()
        => Assert.Contains(Validate(Pepper, "", Environments.Production).Failures!, f => f.Contains("KeyVaultKeyId"));

    [Fact]
    public void Validate_InDevelopment_LetsTheKeyRingGoUnprotected()
        => Assert.True(Validate(Pepper, "", Environments.Development).Succeeded);

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://lazydad-kv.vault.azure.net/keys/DataProtection")]
    [InlineData("https://lazydad-kv.vault.azure.net/secrets/DataProtection")]
    [InlineData("https://lazydad-kv.vault.azure.net/keys/")]
    [InlineData("https://lazydad-kv.vault.azure.net/keys/DataProtection/0123456789abcdef0123456789abcdef")]
    [InlineData("https://lazydad-kv.vault.azure.net/keys/DataProtection?api-version=7.4")]
    public void Validate_RejectsAKeyIdThatIsNotAKeyVaultKey(string keyId)
        => Assert.Contains(Validate(Pepper, keyId, Environments.Development).Failures!, f => f.Contains("KeyVaultKeyId"));

    [Theory]
    [InlineData("GitHub", "client-id", "", true)]
    [InlineData("GitHub", "", "client-secret", true)]
    [InlineData("GitHub", "client-id", "", false)]
    [InlineData("GitHub", "", "client-secret", false)]
    [InlineData("Google", "client-id", "", true)]
    [InlineData("Google", "", "client-secret", false)]
    [InlineData("Telegram", "client-id", "", true)]
    [InlineData("Facebook", "", "client-secret", true)]
    public void Validate_AProvidersClientIdAndSecret_GoTogether_EvenWithSignInOff(string provider, string clientId, string clientSecret, bool signInOn)
    {
        var client = new OAuthClientOptions { ClientId = clientId, ClientSecret = clientSecret };
        var options = new SignInOptions { VoterKeyPepper = signInOn ? Pepper : "" };
        typeof(SignInOptions).GetProperty(provider)!.SetValue(options, client);

        Assert.Contains(Validate(options, KeyId, Environments.Production).Failures!, f => f.Contains($"SignIn:{provider}"));
    }

    private const string Tenant = "3f1a6c2e-5b7d-4e8f-9a0b-1c2d3e4f5a6b";
    private const string App = "6731de76-14a6-49ae-97bc-6eba6914391e";
    private const string Identity = "8d0f1c7e-0000-4000-8000-000000000001";

    [Theory]
    [InlineData(App, Tenant, "secret", "", true)]
    [InlineData(App, Tenant, "", Identity, true)]
    [InlineData("", "", "", "", true)]
    // A secret and a managed identity both: which one proves the app would be a guess.
    [InlineData(App, Tenant, "secret", Identity, false)]
    [InlineData(App, Tenant, "", "", false)]
    [InlineData(App, "", "secret", "", false)]
    [InlineData(App, "contoso.onmicrosoft.com", "secret", "", false)]
    // Entra ids are GUIDs.
    [InlineData("my-app", Tenant, "secret", "", false)]
    [InlineData(App, Tenant, "", "lazydad-production", false)]
    [InlineData("", Tenant, "secret", "", false)]
    [InlineData("", "", "", Identity, false)]
    public void Validate_Microsoft_NeedsItsTenant_AndASecretOrAManagedIdentity(string clientId, string tenantId, string secret,
        string managedIdentity, bool valid)
    {
        var options = new SignInOptions
        {
            VoterKeyPepper = Pepper,
            Microsoft = new() { ClientId = clientId, TenantId = tenantId, ClientSecret = secret, ManagedIdentityClientId = managedIdentity },
        };

        var result = Validate(options, KeyId, Environments.Production);

        Assert.Equal(valid, !(result.Failures ?? []).Any(f => f.Contains("SignIn:Microsoft")));
    }

    [Theory]
    [InlineData("https://lazydad-idp-loadtest.example.io", false, false, true)]
    [InlineData("http://localhost:5299", false, false, true)]
    // The fake signs anyone in: never next to the scheduler (the giveaway of staging or production) or the smoke sign-in.
    [InlineData("https://lazydad-idp-loadtest.example.io", true, false, false)]
    [InlineData("https://lazydad-idp-loadtest.example.io", false, true, false)]
    [InlineData("lazydad-idp-loadtest", false, false, false)]
    public void Validate_TheLoadTestsFakeProvider_OnlyWithoutTheSchedulerOrTheSmokeSignIn(string authority, bool scheduler, bool smoke, bool valid)
    {
        var options = new SignInOptions
        {
            VoterKeyPepper = Pepper,
            LoadTest = new() { Authority = authority },
            Smoke = smoke ? new() { Audience = "api://lazydad-smoke", TenantId = Tenant, AllowedObjectIds = [Identity] } : new(),
        };
        var jokes = new JokeGenerationOptions { Languages = [new() { Language = "Ukrainian", LanguageCode = "uk", Enabled = scheduler }] };

        // Production's environment, without a Key Vault key: the load test's key ring is its own database's.
        var result = new SignInOptionsValidator(Options.Create(new KeyRingOptions()), Options.Create(jokes),
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Production)).Validate(null, options);

        Assert.Equal(valid, result.Succeeded);
    }

    [Theory]
    [InlineData("api://lazydad-smoke", Tenant, Identity, true)]
    [InlineData("", "", "", true)]
    // No identity would trust nobody; a typo must not pass as one.
    [InlineData("api://lazydad-smoke", Tenant, "", false)]
    [InlineData("api://lazydad-smoke", Tenant, "lazydad-github-cd", false)]
    [InlineData("api://lazydad-smoke", "", Identity, false)]
    [InlineData("", Tenant, Identity, false)]
    public void Validate_TheSmokeSignIn_NeedsItsTenant_AndTheIdentitiesItTrusts(string audience, string tenantId, string objectId, bool valid)
    {
        var options = new SignInOptions
        {
            VoterKeyPepper = Pepper,
            Smoke = new() { Audience = audience, TenantId = tenantId, AllowedObjectIds = objectId.Length > 0 ? [objectId] : [] },
        };

        var result = Validate(options, KeyId, Environments.Production);

        Assert.Equal(valid, !(result.Failures ?? []).Any(f => f.Contains("SignIn:Smoke")));
    }
}
