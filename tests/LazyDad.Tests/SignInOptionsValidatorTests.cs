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
    public void Validate_AProvidersClientIdAndSecret_GoTogether_EvenWithSignInOff(string provider, string clientId, string clientSecret, bool signInOn)
    {
        var client = new OAuthClientOptions { ClientId = clientId, ClientSecret = clientSecret };
        var options = new SignInOptions { VoterKeyPepper = signInOn ? Pepper : "" };
        typeof(SignInOptions).GetProperty(provider)!.SetValue(options, client);

        Assert.Contains(Validate(options, KeyId, Environments.Production).Failures!, f => f.Contains($"SignIn:{provider}"));
    }
}
