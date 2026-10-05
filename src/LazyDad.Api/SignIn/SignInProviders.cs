namespace LazyDad.Api.SignIn;

/// <summary>The sign-in providers, by the name that goes into a voter key. Renaming one makes all its voters new.</summary>
public static class SignInProviders
{
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public const string GitHub = "github";
    public const string Facebook = "facebook";
    public const string Telegram = "telegram";

    public static readonly IReadOnlyList<string> All = [Microsoft, Google, GitHub, Facebook, Telegram];
}
