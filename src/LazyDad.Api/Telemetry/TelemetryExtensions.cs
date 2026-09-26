using LazyDad.Api.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LazyDad.Api.Telemetry;

public static class TelemetryExtensions
{
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>
    /// Sends traces, metrics and logs over OTLP (Grafana Cloud in Azure, see infra/deployment-setup.md) when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Without it (local runs, tests, CI) nothing is collected or sent.
    /// The standard <c>OTEL_*</c> settings apply: <c>OTEL_EXPORTER_OTLP_HEADERS</c> carries the credentials, and
    /// <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> defaults to <c>http/protobuf</c> (the spec's default, and what Grafana
    /// Cloud accepts) rather than the .NET exporter's gRPC. Console logs are unchanged; in Azure they stay in
    /// Log Analytics as the fallback.
    /// </summary>
    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<SchedulerMetrics>();

        var configuration = builder.Configuration;
        var endpoint = configuration[EndpointKey];
        if (string.IsNullOrWhiteSpace(endpoint))
            return builder;

        var protocol = string.Equals(configuration[ProtocolKey], "grpc", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.Grpc
            : OtlpExportProtocol.HttpProtobuf;

        builder.Services.AddOpenTelemetry()
            // Container Apps sets CONTAINER_APP_NAME (lazydad-app, lazydad-app-staging) and the replica name, so
            // each environment is its own service ("job" in Grafana), with no setting to forget.
            .ConfigureResource(resource => resource.AddService(
                serviceName: configuration["CONTAINER_APP_NAME"] ?? "lazydad-local",
                serviceVersion: configuration[$"{AppInfoOptions.SectionName}:{nameof(AppInfoOptions.Version)}"] ?? "dev",
                serviceInstanceId: configuration["CONTAINER_APP_REPLICA_NAME"] ?? Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddSource(LazyDadTelemetry.Name, LazyDadTelemetry.ChatClientName)
                // Uptime checks poll /healthz; they'd flood the traces. Its request metrics stay.
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/healthz"))
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                // Added before the exporter (UseOtlpExporter below), so it runs first.
                .AddProcessor(new PersonalDataFilter()))
            .WithMetrics(metrics => metrics
                .AddMeter(LazyDadTelemetry.Name, LazyDadTelemetry.ChatClientName, "System.Runtime")
                // Includes the rate limiter's meter: rejected votes show up there.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation())
            .WithLogging(logging => { }, options => options.IncludeFormattedMessage = true)
            .UseOtlpExporter(protocol, new Uri(endpoint));

        return builder;
    }
}
