using Microsoft.AspNetCore.Authentication;

namespace LazyDad.Api.SignIn;

/// <summary>
/// The providers a reader can sign in with here: the authentication schemes registered under a provider's name (see
/// SignInSetup), so the list and the handlers can't disagree. Only these appear in metrics, so their tags stay bounded.
/// </summary>
public sealed class EnabledSignInProviders
{
    public EnabledSignInProviders(IAuthenticationSchemeProvider schemes)
        // The schemes are registered at startup and never change, so the default provider's task is already complete.
        : this(schemes.GetAllSchemesAsync().GetAwaiter().GetResult().Select(s => s.Name).Where(SignInProviders.IsKnown).ToList())
    {
    }

    /// <summary>For tests: a fixed list.</summary>
    internal EnabledSignInProviders(IReadOnlyList<string> names)
    {
        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    public bool Contains(string provider) => Names.Contains(provider);
}
