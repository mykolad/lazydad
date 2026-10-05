using LazyDad.Api.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class SignInOptionsValidatorTests
{
    private const string KeyId = "https://lazydad-kv.vault.azure.net/keys/DataProtection";
    private static readonly string Pepper = Convert.ToBase64String(new byte[SignInOptions.MinPepperBytes]);

    private static ValidateOptionsResult Validate(string pepper, string keyId, string environment)
        => new SignInOptionsValidator(
                Options.Create(new KeyRingOptions { KeyVaultKeyId = keyId }),
                Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environment))
            .Validate(null, new SignInOptions { VoterKeyPepper = pepper });

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
    public void Validate_RejectsAKeyIdThatIsNotAKeyVaultKey(string keyId)
        => Assert.Contains(Validate(Pepper, keyId, Environments.Development).Failures!, f => f.Contains("KeyVaultKeyId"));
}
