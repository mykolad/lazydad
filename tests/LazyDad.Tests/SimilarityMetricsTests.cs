using System.Diagnostics.Metrics;
using LazyDad.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace LazyDad.Tests;

public sealed class SimilarityMetricsTests : IDisposable
{
    private readonly ServiceProvider metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => metricsProvider.Dispose();

    [Fact]
    public void JevCredits_AreReportedUntilTheReadingIsSixHoursOld()
    {
        var meters = metricsProvider.GetRequiredService<IMeterFactory>();
        var metrics = new SimilarityMetrics(meters, clock);
        using var credits = new MetricCollector<double>(meters, LazyDadTelemetry.Name, "lazydad.jev.credits");

        credits.RecordObservableInstruments();
        Assert.Empty(credits.GetMeasurementSnapshot());

        metrics.RecordJevCredits(3.5);
        clock.Advance(SimilarityMetrics.CreditsFreshFor - TimeSpan.FromMinutes(1));
        credits.RecordObservableInstruments();
        Assert.Equal(3.5, Assert.Single(credits.GetMeasurementSnapshot(clear: true)).Value);

        // Another replica may have heard a newer balance since: an old reading stops, rather than standing for it.
        clock.Advance(TimeSpan.FromMinutes(1));
        credits.RecordObservableInstruments();
        Assert.Empty(credits.GetMeasurementSnapshot());
    }
}
