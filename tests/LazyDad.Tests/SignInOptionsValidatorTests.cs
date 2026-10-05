using LazyDad.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public class SignInOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(string pepper)
        => new SignInOptionsValidator().Validate(null, new SignInOptions { VoterKeyPepper = pepper });

    [Fact]
    public void Validate_WithoutAPepper_Passes() => Assert.True(Validate("").Succeeded);

    [Fact]
    public void Validate_ThirtyTwoBytes_Pass()
        => Assert.True(Validate(Convert.ToBase64String(new byte[SignInOptions.MinPepperBytes])).Succeeded);

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")]
    public void Validate_RejectsAPepperThatIsNotBase64OrTooShort(string pepper)
        => Assert.Contains(Validate(pepper).Failures!, f => f.Contains("VoterKeyPepper"));
}
