using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Data.Entities;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public class VoterKeysTests
{
    private static readonly string Pepper = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private static VoterKeys Keys(string pepper) => new(Options.Create(new SignInOptions { VoterKeyPepper = pepper }));

    [Fact]
    public void For_TheSameAccount_GivesTheSameKey()
    {
        var keys = Keys(Pepper);

        var key = keys.For(SignInProviders.GitHub, "12345");

        Assert.Equal(Vote.VoterKeyLength, key.Length);
        Assert.Equal(key, keys.For(SignInProviders.GitHub, "12345"));
        Assert.Equal(key, Keys(Pepper).For(SignInProviders.GitHub, "12345"));
    }

    [Fact]
    public void For_TheSameIdAtAnotherProvider_IsAnotherVoter()
        => Assert.NotEqual(Keys(Pepper).For(SignInProviders.GitHub, "12345"), Keys(Pepper).For(SignInProviders.Telegram, "12345"));

    [Fact]
    public void For_AnotherAccount_IsAnotherVoter()
        => Assert.NotEqual(Keys(Pepper).For(SignInProviders.Google, "12345"), Keys(Pepper).For(SignInProviders.Google, "12346"));

    [Fact]
    public void For_WithAnotherPepper_GivesAnotherKey()
    {
        var otherPepper = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

        Assert.NotEqual(Keys(Pepper).For(SignInProviders.Facebook, "12345"), Keys(otherPepper).For(SignInProviders.Facebook, "12345"));
    }

    [Fact]
    public void For_DoesNotContainTheAccountId()
    {
        // Not a plain hash or an encoding of the id: SHA-256 of the same text differs.
        var key = Keys(Pepper).For(SignInProviders.GitHub, "12345");

        Assert.NotEqual(System.Security.Cryptography.SHA256.HashData("github:12345"u8), key);
    }

    [Fact]
    public void For_RejectsAnUnknownProvider()
        => Assert.Throws<ArgumentException>(() => Keys(Pepper).For("x", "12345"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void For_RejectsAnEmptyAccountId(string accountId)
        => Assert.Throws<ArgumentException>(() => Keys(Pepper).For(SignInProviders.Microsoft, accountId));

    [Fact]
    public void WithoutAPepper_SignInIsOff()
    {
        var keys = Keys("");

        Assert.False(keys.Enabled);
        Assert.Throws<InvalidOperationException>(() => keys.For(SignInProviders.Microsoft, "12345"));
    }
}
