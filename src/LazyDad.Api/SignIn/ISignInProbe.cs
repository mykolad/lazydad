namespace LazyDad.Api.SignIn;

/// <summary>Checks a provider's registration without a reader: see <see cref="OAuthCodeProbe"/>.</summary>
public interface ISignInProbe
{
    string Provider { get; }

    Task<ProviderState> ProbeAsync(CancellationToken cancellationToken);
}
