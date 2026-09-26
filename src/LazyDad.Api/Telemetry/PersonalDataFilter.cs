using System.Diagnostics;
using OpenTelemetry;

namespace LazyDad.Api.Telemetry;

/// <summary>
/// Removes span attributes that could identify a visitor before spans are exported: the site keeps no personal
/// data, and telemetry leaves Azure. Not every instrumentation in use sets these today, but an update could start to.
/// </summary>
public sealed class PersonalDataFilter : BaseProcessor<Activity>
{
    internal static readonly string[] RemovedTags =
    [
        // The caller: behind UseForwardedHeaders, the visitor's IP.
        "client.address", "client.port",
        // The other end of the connection.
        "network.peer.address", "network.peer.port",
        // The browser's user agent, which helps fingerprint a visitor.
        "user_agent.original",
        // A signed-in user (none yet; sign-in would come from an external identity provider).
        "enduser.id",
        // The same, under older semantic conventions.
        "http.client_ip", "http.user_agent", "net.peer.ip", "net.sock.peer.addr",
    ];

    public override void OnEnd(Activity data)
    {
        foreach (var tag in RemovedTags)
            data.SetTag(tag, null);
    }
}
