using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Data;
using System.Text.Json;
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
        Assert.Equal(KeyRingState.Pending, check.State);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(KeyRingState.Ok, check.State);
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

        Assert.Equal(KeyRingState.Failed, check.State);
    }

    [Fact]
    public async Task WithSignInOff_ChecksNothing()
    {
        var provider = new Mock<IDataProtectionProvider>(MockBehavior.Strict);
        var check = Check(provider.Object, Off);

        await check.RunAsync(CancellationToken.None);

        Assert.Equal(KeyRingState.Off, check.State);
    }

    [Fact]
    public async Task AResult_IsSetOnlyOnce()
    {
        // A second run (say, after the ring stops working) doesn't turn an "ok" into "failed", or back.
        var provider = new FailsAfterFirstUse();
        var check = Check(provider, On);

        await check.RunAsync(CancellationToken.None);
        await check.RunAsync(CancellationToken.None);

        Assert.Equal(2, provider.Uses);
        Assert.Equal(KeyRingState.Ok, check.State);
    }

    [Theory]
    [InlineData(KeyRingState.Off, "off")]
    [InlineData(KeyRingState.Pending, "pending")]
    [InlineData(KeyRingState.Ok, "ok")]
    [InlineData(KeyRingState.Failed, "failed")]
    public void State_ShowsOnStatusByItsName(KeyRingState state, string name)
        // What /status (and the smoke tests reading it) see.
        => Assert.Equal($$"""{"keyRing":"{{name}}"}""", JsonSerializer.Serialize(new { keyRing = state }));

    private sealed class FailsAfterFirstUse : IDataProtectionProvider
    {
        private readonly IDataProtectionProvider working = new EphemeralDataProtectionProvider();

        public int Uses { get; private set; }

        public IDataProtector CreateProtector(string purpose)
            => ++Uses == 1 ? working.CreateProtector(purpose) : throw new InvalidOperationException("403 from Key Vault");
    }
}
