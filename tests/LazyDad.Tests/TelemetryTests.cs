using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
            ["CONTAINER_APP_REPLICA_NAME"] = "lazydad-app-under-test--r1-replica-7",
        });
        builder.AddTelemetry();
        await using var app = builder.Build();
        app.MapGet("/probe", (ILogger<TelemetryTests> logger) =>
        {
            // As if an instrumentation had recorded the visitor: the filter must drop it before export.
            Activity.Current?.SetTag("client.address", "203.0.113.7");
            Activity.Current?.SetTag("enduser.id", "VoterKeyQmFzZTY0VmFsdWU=");
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
            http.DefaultRequestHeaders.Add("Cookie", "__Host-lazydad=SignInCookieValue");
            // As a provider's callback (/signin-github?code=…&state=…) arrives.
            Assert.Equal("ok", await http.GetStringAsync("/probe?code=SignInCodeValue&state=SignInStateValue"));
            Assert.Equal("healthy", await http.GetStringAsync("/healthz"));
        }
        Assert.True(app.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());
        Assert.True(app.Services.GetRequiredService<LoggerProvider>().ForceFlush());
        // A flush can finish with its export still being retried (e.g. on a busy CI runner): wait for all three.
        await collector.WaitForAsync(["/otlp/v1/traces", "/otlp/v1/metrics", "/otlp/v1/logs"], TimeSpan.FromSeconds(30));
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
        // Grafana's host for Application Observability is the replica: in protobuf the attribute's value follows its key
        // after a few framing bytes.
        Assert.Matches(new Regex(@"grafana\.host\.id.{1,8}lazydad-app-under-test--r1-replica-7", RegexOptions.Singleline), traces);
        Assert.DoesNotContain("203.0.113.7", traces);
        Assert.DoesNotContain("FingerprintBrowser", traces);
        // Nothing about who's signed in: not the cookie, not a voter key.
        Assert.DoesNotContain("SignInCookieValue", traces);
        Assert.DoesNotContain("VoterKey", traces);
        Assert.DoesNotContain("SignInCodeValue", traces);
        Assert.DoesNotContain("SignInStateValue", traces);
        Assert.DoesNotContain("/healthz", traces);
        Assert.Contains("Probe says hello-otlp", collector.Body("/otlp/v1/logs"));
        Assert.Contains("http.server.request.duration", collector.Body("/otlp/v1/metrics"));
    }

    [Fact]
    public async Task SchedulerMetrics_ExportsTheStartingZerosBeforeAnyTick()
    {
        await using var collector = await FakeCollector.StartAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [TelemetryExtensions.EndpointKey] = $"{collector.Url}/otlp" });
        builder.AddTelemetry();
        await using var app = builder.Build();
        var metrics = app.Services.GetRequiredService<SchedulerMetrics>();

        // What the scheduler does at startup: the zeros go out in an export of their own, before any tick.
        metrics.Initialize("Ukrainian", ["fast"]);
        await metrics.ExportNowAsync();
        await collector.WaitForAsync(["/otlp/v1/metrics"], TimeSpan.FromSeconds(30));
        var first = Assert.Single(collector.Bodies("/otlp/v1/metrics"));
        Assert.Contains("lazydad.scheduler.ticks", first);
        Assert.Contains("skipped", first);       // a series no tick has touched yet: it can only be one of the zeros
        Assert.Contains("lazydad.jokes", first);

        // A tick right after: its 1 arrives in a later export, on top of the 0 Grafana already has. Not necessarily the
        // second one to arrive: CI once got an export in between without the app's metrics.
        metrics.RecordTick("Ukrainian", "failed");
        Assert.True(app.Services.GetRequiredService<MeterProvider>().ForceFlush());
        await collector.WaitUntilAsync(() => collector.Bodies("/otlp/v1/metrics").Skip(1).Any(b => b.Contains("lazydad.scheduler.ticks")),
            TimeSpan.FromSeconds(30));
        Assert.Contains(collector.Bodies("/otlp/v1/metrics").Skip(1), b => b.Contains("lazydad.scheduler.ticks"));
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

        public async Task WaitForAsync(string[] paths, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!paths.All(Paths.Contains) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
        }

        public async Task WaitUntilAsync(Func<bool> arrived, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!arrived() && DateTime.UtcNow < deadline)
                await Task.Delay(50);
        }

        public string Body(string path) => string.Concat(Bodies(path));

        /// <summary>Each export to <paramref name="path"/>, in the order they arrived.</summary>
        public IReadOnlyList<string> Bodies(string path)
        {
            lock (requests)
                return requests.Where(r => r.Path == path).Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
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
