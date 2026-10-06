using System.Diagnostics;
using LazyDad.Api.Telemetry;

namespace LazyDad.Api.SignIn;

/// <summary>
/// Probes every configured provider at startup and then every <see cref="Interval"/>, so <c>/status</c>, the smoke tests
/// and the alerts know whether its registration works before a reader finds out. Each probe is one token request, a few
/// a hour per app.
/// </summary>
public sealed class SignInProbeService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IReadOnlyList<ISignInProbe> probes;
    private readonly SignInProviderStatus status;
    private readonly SignInMetrics metrics;
    private readonly ILogger<SignInProbeService> logger;
    private readonly TimeSpan interval;

    public SignInProbeService(IEnumerable<ISignInProbe> probes, SignInProviderStatus status, SignInMetrics metrics,
        ILogger<SignInProbeService> logger)
        : this(probes, status, metrics, logger, Interval)
    {
    }

    /// <summary>For tests: a shorter interval.</summary>
    internal SignInProbeService(IEnumerable<ISignInProbe> probes, SignInProviderStatus status, SignInMetrics metrics,
        ILogger<SignInProbeService> logger, TimeSpan interval)
    {
        this.probes = probes.ToList();
        this.status = status;
        this.metrics = metrics;
        this.logger = logger;
        this.interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (probes.Count == 0)
            return;
        try
        {
            while (true)
            {
                await Task.WhenAll(probes.Select(probe => ProbeAsync(probe, stoppingToken)));
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeAsync(ISignInProbe probe, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var state = await probe.ProbeAsync(stoppingToken);
            status.Set(probe.Provider, state);
            metrics.RecordProviderCall(probe.Provider, SignInBackchannel.Probe, state.ToString().ToLowerInvariant(), Stopwatch.GetElapsedTime(started));
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // A probe's own bug must not stop the app (a background service's exception does) nor the other probes.
            logger.LogError(ex, "The {Provider} sign-in probe failed.", probe.Provider);
        }
    }
}
