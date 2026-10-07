namespace LazyDad.Api.SignIn;

/// <summary>
/// The sign-in providers, by the name that goes into a voter key and names the provider's authentication scheme.
/// Renaming one makes all its voters new.
/// </summary>
public static class SignInProviders
{
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public const string GitHub = "github";
    public const string Facebook = "facebook";
    public const string Telegram = "telegram";

    /// <summary>Signs in at once as a made-up account, with no provider: registered only when the environment is Development.</summary>
    public const string Development = "dev";

    public static readonly IReadOnlyList<string> All = [Microsoft, Google, GitHub, Facebook, Telegram];

    /// <summary>
    /// How the sign-in dialog lists them: GitHub first, as the first provider (#72), then the rest; the made-up
    /// Development account last, so a local run shows the real order.
    /// </summary>
    public static readonly IReadOnlyList<string> DisplayOrder = [GitHub, Google, Microsoft, Telegram, Facebook, Development];

    public static bool IsKnown(string provider) => All.Contains(provider) || provider == Development;
}
