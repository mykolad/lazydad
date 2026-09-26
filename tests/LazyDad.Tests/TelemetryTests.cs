using System.Diagnostics;
using System.Text;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace LazyDad.Tests;

public class TelemetryTests
{
    [Fact]
    public void PersonalDataFilter_RemovesVisitorAttributes_AndKeepsTheRest()
    {
        using var activity = new Activity("GET /jokes/feed");
        activity.SetTag("client.address", "203.0.113.7");
        activity.SetTag("network.peer.address", "203.0.113.7");
        activity.SetTag("user_agent.original", "Mozilla/5.0");
        activity.SetTag("http.route", "jokes/feed");

        new PersonalDataFilter().OnEnd(activity);

        Assert.Equal([new KeyValuePair<string, object?>("http.route", "jokes/feed")], activity.TagObjects);
    }

    [Fact]
    public async Task AddTelemetry_WithoutAnEndpoint_CollectsNothing()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [TelemetryExtensions.EndpointKey] = "" });

        builder.AddTelemetry();
        await using var app = builder.Build();

        Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.Null(app.Services.GetService<MeterProvider>());
        // The scheduler's counters are always there; without a listener they cost nothing.
        Assert.NotNull(app.Services.GetService<SchedulerMetrics>());
    }

    [Fact]
    public async Task AddTelemetry_WithAnEndpoint_ExportsOverOtlpHttp_WithoutVisitorData()
    {
        await using var collector = await FakeCollector.StartAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TelemetryExtensions.EndpointKey] = $"{collector.Url}/otlp",
            // What Grafana Cloud's setup page shows: basic auth, the space URL-encoded as the spec requires.
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Basic%20aW5zdGFuY2U6dG9rZW4=",
            ["CONTAINER_APP_NAME"] = "lazydad-app-under-test",
        });
        builder.AddTelemetry();
        await using var app = builder.Build();
        app.MapGet("/probe", (ILogger<TelemetryTests> logger) =>
        {
            // As if an instrumentation had recorded the visitor: the filter must drop it before export.
            Activity.Current?.SetTag("client.address", "203.0.113.7");
            Activity.Current?.SetTag("probe.kept", "kept-value");
            logger.LogInformation("Probe says {Word}", "hello-otlp");
            return "ok";
        });
        app.MapGet("/healthz", () => "healthy");
        await app.StartAsync();

        // The test's own client calls aren't traced (they would be: the app's HttpClient instrumentation is process-wide),
        // so the traces hold only the app's server spans.
        using (SuppressInstrumentationScope.Begin())
        using (var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FingerprintBrowser/1.0");
            Assert.Equal("ok", await http.GetStringAsync("/probe"));
            Assert.Equal("healthy", await http.GetStringAsync("/healthz"));
        }
        Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<LoggerProvider>().ForceFlush());
        await app.StopAsync();

        // http/protobuf by default (not the .NET exporter's gRPC), with the signal paths appended to the base URL.
        Assert.Contains("/otlp/v1/traces", collector.Paths);
        Assert.Contains("/otlp/v1/metrics", collector.Paths);
        Assert.Contains("/otlp/v1/logs", collector.Paths);
        Assert.All(collector.AuthorizationHeaders, header => Assert.Equal("Basic aW5zdGFuY2U6dG9rZW4=", header));

        // Protobuf keeps strings as UTF-8, so the payloads can be searched as text.
        var traces = collector.Body("/otlp/v1/traces");
        Assert.Contains("kept-value", traces);
        Assert.Contains("lazydad-app-under-test", traces);
        Assert.DoesNotContain("203.0.113.7", traces);
        Assert.DoesNotContain("FingerprintBrowser", traces);
        Assert.DoesNotContain("/healthz", traces);
        Assert.Contains("Probe says hello-otlp", collector.Body("/otlp/v1/logs"));
        Assert.Contains("http.server.request.duration", collector.Body("/otlp/v1/metrics"));
    }

    /// <summary>Accepts OTLP/HTTP exports and keeps what arrived.</summary>
    private sealed class FakeCollector : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly List<(string Path, string? Authorization, byte[] Body)> requests = [];

        private FakeCollector(WebApplication app) => this.app = app;

        public string Url => app.Urls.First();

        public IReadOnlyList<string> Paths { get { lock (requests) return requests.Select(r => r.Path).ToList(); } }

        public IReadOnlyList<string?> AuthorizationHeaders { get { lock (requests) return requests.Select(r => r.Authorization).ToList(); } }

        public string Body(string path)
        {
            lock (requests)
                return string.Concat(requests.Where(r => r.Path == path).Select(r => Encoding.UTF8.GetString(r.Body)));
        }

        public static async Task<FakeCollector> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var collector = new FakeCollector(app);
            app.MapPost("/{**path}", async (HttpContext context) =>
            {
                using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body);
                lock (collector.requests)
                    collector.requests.Add((context.Request.Path, context.Request.Headers.Authorization.ToString(), body.ToArray()));
                return Results.Ok();
            });
            await app.StartAsync();
            return collector;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
