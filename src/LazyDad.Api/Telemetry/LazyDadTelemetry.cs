using System.Diagnostics;

namespace LazyDad.Api.Telemetry;

/// <summary>The app's own trace and metric names (see <see cref="TelemetryExtensions"/>).</summary>
public static class LazyDadTelemetry
{
    /// <summary>The name of the app's <see cref="ActivitySource"/> and of its meter (<see cref="SchedulerMetrics"/>).</summary>
    public const string Name = "LazyDad";

    /// <summary>
    /// Microsoft.Extensions.AI's trace source and meter (<c>UseOpenTelemetry()</c> on the chat clients): a span per
    /// LLM call, plus the <c>gen_ai.client.*</c> duration and token usage metrics.
    /// </summary>
    public const string ChatClientName = "Experimental.Microsoft.Extensions.AI";

    public static readonly ActivitySource ActivitySource = new(Name);
}
