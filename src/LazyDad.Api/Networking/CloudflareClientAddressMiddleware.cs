using System.Net;
using LazyDad.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Networking;

/// <summary>
/// Behind Cloudflare's proxy, the caller that Container Apps' ingress reports (the last X-Forwarded-For hop, which
/// <c>UseForwardedHeaders</c> applies) is a Cloudflare edge server, shared by many visitors. For those requests only, the
/// visitor's address is Cloudflare's <c>CF-Connecting-IP</c> header, which Cloudflare always sets itself. Anyone can send
/// that header directly, so it counts only when the request really came from one of Cloudflare's ranges. The vote rate
/// limit partitions by the result; nothing stores it.
/// </summary>
public sealed class CloudflareClientAddressMiddleware
{
    public const string HeaderName = "CF-Connecting-IP";

    private readonly RequestDelegate next;
    private readonly IPNetwork[] ranges;

    public CloudflareClientAddressMiddleware(RequestDelegate next, IOptions<CloudflareOptions> options)
    {
        this.next = next;
        ranges = options.Value.IpRanges.Select(IPNetwork.Parse).ToArray();
    }

    public Task InvokeAsync(HttpContext context)
    {
        var caller = context.Connection.RemoteIpAddress;
        // A single, valid address only: a repeated header reads as a comma-separated list and doesn't parse.
        if (caller is not null && IsCloudflare(caller)
            && IPAddress.TryParse(context.Request.Headers[HeaderName].ToString(), out var visitor))
        {
            context.Connection.RemoteIpAddress = visitor;
        }
        return next(context);
    }

    internal bool IsCloudflare(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return ranges.Any(range => range.Contains(normalized));
    }
}
