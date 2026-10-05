using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public sealed class KeyRingCheckTests : IDisposable
{
    private static readonly VoterKeys On = new(Options.Create(new SignInOptions { VoterKeyPepper = Convert.ToBase64String(new byte[32]) }));
    private static readonly VoterKeys Off = new(Options.Create(new SignInOptions()));

    private readonly TestDatabase database = new();

    public void Dispose() => database.Dispose();

    private static KeyRingCheck Check(IDataProtectionProvider provider, VoterKeys voterKeys)
        => new(provider, voterKeys, NullLogger<KeyRingCheck>.Instance);

    [Fact]
    public async Task WithTheRingInTheDatabase_IsOk_AndMadeItsFirstKey()
    {
        using var services = new ServiceCollection()
            .AddDbContext<LazyDadDbContext>(database.Configure)
            .AddDataProtection().SetApplicationName(SignInSetup.ApplicationName).PersistKeysToDbContext<LazyDadDbContext>()
            .Services.BuildServiceProvider();
        var check = Check(services.GetRequiredService<IDataProtectionProvider>(), On);
        Assert.Equal(KeyRingCheck.Pending, check.State);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(KeyRingCheck.Ok, check.State);
        await using var context = database.CreateContext();
        Assert.Single(await context.DataProtectionKeys.ToListAsync());
    }

    [Fact]
    public async Task WhenTheRingFails_SaysSo_WithoutThrowing()
    {
        // E.g. the app may not use the Key Vault key: unwrapping throws.
        var provider = new Mock<IDataProtectionProvider>();
        provider.Setup(p => p.CreateProtector(It.IsAny<string>())).Throws(new InvalidOperationException("403 from Key Vault"));
        var check = Check(provider.Object, On);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(KeyRingCheck.Failed, check.State);
    }

    [Fact]
    public async Task WithSignInOff_ChecksNothing()
    {
        var provider = new Mock<IDataProtectionProvider>(MockBehavior.Strict);
        var check = Check(provider.Object, Off);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(KeyRingCheck.Off, check.State);
    }
}
