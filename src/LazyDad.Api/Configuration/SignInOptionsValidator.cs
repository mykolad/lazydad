using Microsoft.Extensions.Options;

namespace LazyDad.Api.Configuration;

/// <summary>Validated at startup (ValidateOnStart), so a pepper that's too short or isn't base64 never makes a voter key.</summary>
public class SignInOptionsValidator : IValidateOptions<SignInOptions>
{
    public ValidateOptionsResult Validate(string? name, SignInOptions options)
    {
        if (!options.Enabled)
            return ValidateOptionsResult.Success;

        var pepper = new byte[options.VoterKeyPepper.Length];
        if (!Convert.TryFromBase64String(options.VoterKeyPepper, pepper, out var length) || length < SignInOptions.MinPepperBytes)
            return ValidateOptionsResult.Fail(
                $"{SignInOptions.SectionName}:VoterKeyPepper must be base64 of at least {SignInOptions.MinPepperBytes} random bytes.");
        return ValidateOptionsResult.Success;
    }
}
