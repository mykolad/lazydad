namespace LazyDad.Api.Configuration;

/// <summary>
/// The site is served through Cloudflare's proxy (runbook section 10). <see cref="IpRanges"/> is Cloudflare's published
/// list (https://www.cloudflare.com/ips/): requests from these addresses are Cloudflare's edge servers.
/// </summary>
public class CloudflareOptions
{
    public const string SectionName = "Cloudflare";

    /// <summary>CIDR ranges, IPv4 and IPv6. Cloudflare rarely changes them; keep them in sync with the ingress rules.</summary>
    public List<string> IpRanges { get; set; } = [];
}
