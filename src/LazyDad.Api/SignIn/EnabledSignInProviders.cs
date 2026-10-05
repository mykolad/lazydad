namespace LazyDad.Api.SignIn;

/// <summary>
/// The providers a reader can sign in with here, decided at startup (each needs sign-in on and its own settings).
/// Only these appear in metrics, so their tags stay bounded.
/// </summary>
public sealed class EnabledSignInProviders
{
    public EnabledSignInProviders(IReadOnlyList<string> names)
    {
        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    public bool Contains(string provider) => Names.Contains(provider);
}
