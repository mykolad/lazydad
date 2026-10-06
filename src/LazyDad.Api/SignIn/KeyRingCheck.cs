using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Where <see cref="KeyRingCheck"/> stands. On <c>/status</c> as <c>off</c>, <c>pending</c>, <c>ok</c> or <c>failed</c>
/// (the smoke tests read those names).
/// </summary>
[JsonConverter(typeof(KeyRingStateJsonConverter))]
public enum KeyRingState
{
    /// <summary>Sign-in is off (no pepper): nothing to check. Final.</summary>
    Off,
    /// <summary>The check hasn't finished yet.</summary>
    Pending,
    /// <summary>Final.</summary>
    Ok,
    /// <summary>Final.</summary>
    Failed,
}

public sealed class KeyRingStateJsonConverter() : JsonStringEnumConverter<KeyRingState>(JsonNamingPolicy.CamelCase);

/// <summary>
/// Proves at startup that the cookies' key ring works, before anyone signs in: one value encrypted and decrypted
/// through it (the first time, that makes the ring's first key: written to the database, wrapped with the Key Vault
/// key). A missing role or a wrong key id shows as <c>failed</c> on <c>/status</c>, which the smoke tests check,
/// instead of as a broken sign-in. It never stops the app: everything but sign-in works without the ring.
/// The state only ever moves once, from <see cref="KeyRingState.Pending"/> to <see cref="KeyRingState.Ok"/> or
/// <see cref="KeyRingState.Failed"/>; <see cref="KeyRingState.Off"/> is set at the start and never changes.
/// </summary>
public sealed class KeyRingCheck
{
    private const string Probe = "ok";

    private readonly IDataProtectionProvider dataProtection;
    private readonly VoterKeys voterKeys;
    private readonly ILogger<KeyRingCheck> logger;
    // A KeyRingState, as an int so Interlocked can change it.
    private int state;

    public KeyRingCheck(IDataProtectionProvider dataProtection, VoterKeys voterKeys, ILogger<KeyRingCheck> logger)
    {
        this.dataProtection = dataProtection;
        this.voterKeys = voterKeys;
        this.logger = logger;
        state = (int)(voterKeys.Enabled ? KeyRingState.Pending : KeyRingState.Off);
    }

    public KeyRingState State => (KeyRingState)Volatile.Read(ref state);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!voterKeys.Enabled)
            return;
        try
        {
            // Off the startup path: the database may be resuming (staging), and Key Vault is a network call.
            var ok = await Task.Run(() =>
            {
                var protector = dataProtection.CreateProtector("LazyDad.KeyRingCheck");
                return protector.Unprotect(protector.Protect(Probe)) == Probe;
            }, cancellationToken);
            Finish(ok ? KeyRingState.Ok : KeyRingState.Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Finish(KeyRingState.Failed);
            logger.LogError(ex, "The sign-in cookies' key ring doesn't work: check DataProtection:KeyVaultKeyId and the app's role on that key");
        }
    }

    // Only from Pending: a result, once set, stays.
    private void Finish(KeyRingState result)
        => Interlocked.CompareExchange(ref state, (int)result, (int)KeyRingState.Pending);
}
