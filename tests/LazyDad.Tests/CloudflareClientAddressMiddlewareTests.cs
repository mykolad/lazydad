using System.Net;
using LazyDad.Api.Configuration;
using LazyDad.Api.Networking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public class CloudflareClientAddressMiddlewareTests
{
    private const string CloudflareEdge = "172.64.10.20";   // in 172.64.0.0/13
    private const string Visitor = "203.0.113.7";

    private static readonly CloudflareOptions Ranges = new() { IpRanges = ["172.64.0.0/13", "2606:4700::/32"] };

    /// <summary>Runs the middleware for a request from <paramref name="caller"/>; returns the address the app then sees.</summary>
    private static async Task<IPAddress?> ClientAddressAsync(string caller, params string[] headerValues)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(caller);
        if (headerValues.Length > 0)
            context.Request.Headers[CloudflareClientAddressMiddleware.HeaderName] = headerValues;

        IPAddress? seen = null;
        var middleware = new CloudflareClientAddressMiddleware(c => { seen = c.Connection.RemoteIpAddress; return Task.CompletedTask; }, Options.Create(Ranges));
        await middleware.InvokeAsync(context);
        return seen;
    }

    [Fact]
    public async Task FromCloudflare_UsesTheVisitorFromTheHeader()
        => Assert.Equal(IPAddress.Parse(Visitor), await ClientAddressAsync(CloudflareEdge, Visitor));

    [Fact]
    public async Task FromCloudflare_AcceptsAnIPv6Visitor()
        => Assert.Equal(IPAddress.Parse("2001:db8::1"), await ClientAddressAsync(CloudflareEdge, "2001:db8::1"));

    [Fact]
    public async Task FromCloudflareOverIPv6_UsesTheVisitor()
        => Assert.Equal(IPAddress.Parse(Visitor), await ClientAddressAsync("2606:4700::1111", Visitor));

    [Fact]
    public async Task FromCloudflareAsAnIPv4MappedAddress_UsesTheVisitor()
        => Assert.Equal(IPAddress.Parse(Visitor), await ClientAddressAsync("::ffff:" + CloudflareEdge, Visitor));

    [Fact]
    public async Task NotFromCloudflare_IgnoresTheHeader()
    {
        // Someone calling the app directly can send the header too; it must not choose their rate-limit bucket.
        Assert.Equal(IPAddress.Parse("198.51.100.9"), await ClientAddressAsync("198.51.100.9", Visitor));
    }

    [Theory]
    [InlineData]
    [InlineData("not-an-address")]
    [InlineData("")]
    public async Task FromCloudflare_WithoutAUsableHeader_KeepsTheCaller(params string[] headerValues)
        => Assert.Equal(IPAddress.Parse(CloudflareEdge), await ClientAddressAsync(CloudflareEdge, headerValues));

    [Fact]
    public async Task FromCloudflare_WithTheHeaderRepeated_KeepsTheCaller()
        => Assert.Equal(IPAddress.Parse(CloudflareEdge), await ClientAddressAsync(CloudflareEdge, Visitor, "198.51.100.9"));

    [Fact]
    public void AppSettings_ListCloudflaresPublishedRanges()
    {
        // The configured list is what the middleware trusts; it must parse and cover both address families.
        var options = new CloudflareOptions();
        new ConfigurationBuilder().AddJsonFile("appsettings.json").Build()
            .GetSection(CloudflareOptions.SectionName).Bind(options);

        var ranges = options.IpRanges.Select(IPNetwork.Parse).ToList();
        Assert.Contains(ranges, r => r.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        Assert.Contains(ranges, r => r.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        Assert.Contains(ranges, r => r.Contains(IPAddress.Parse("104.16.0.1")));
    }
}
