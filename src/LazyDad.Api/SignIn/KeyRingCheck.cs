using Microsoft.AspNetCore.DataProtection;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Proves at startup that the cookies' key ring works, before anyone signs in: one value encrypted and decrypted
/// through it (the first time, that makes the ring's first key: written to the database, wrapped with the Key Vault
/// key). A missing role or a wrong key id shows as <c>failed</c> on <c>/status</c>, which the smoke tests check,
/// instead of as a broken sign-in. It never stops the app: everything but sign-in works without the ring.
/// </summary>
public sealed class KeyRingCheck
{
    public const string Off = "off";
    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string Failed = "failed";

    private readonly IDataProtectionProvider dataProtection;
    private readonly VoterKeys voterKeys;
    private readonly ILogger<KeyRingCheck> logger;
    private string state;

    public KeyRingCheck(IDataProtectionProvider dataProtection, VoterKeys voterKeys, ILogger<KeyRingCheck> logger)
    {
        this.dataProtection = dataProtection;
        this.voterKeys = voterKeys;
        this.logger = logger;
        state = voterKeys.Enabled ? Pending : Off;
    }

    /// <summary><c>off</c> (sign-in is off), <c>pending</c>, <c>ok</c> or <c>failed</c>.</summary>
    public string State => Volatile.Read(ref state);

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
                return protector.Unprotect(protector.Protect(Ok)) == Ok;
            }, cancellationToken);
            Volatile.Write(ref state, ok ? Ok : Failed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Volatile.Write(ref state, Failed);
            logger.LogError(ex, "The sign-in cookies' key ring doesn't work: check DataProtection:KeyVaultKeyId and the app's role on that key");
        }
    }
}
