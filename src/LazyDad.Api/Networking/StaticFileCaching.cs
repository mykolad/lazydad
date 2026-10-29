namespace LazyDad.Api.Networking;

/// <summary>
/// How long browsers (and Cloudflare) may keep each file in <c>wwwroot</c>. The shell asks for <c>app.js</c> and
/// <c>app.css</c> with <c>?v=&lt;version&gt;</c>, so a deploy changes their URLs: those are kept for a year without
/// asking again. The shell itself is asked for again on every visit, or a deploy wouldn't reach anyone.
/// </summary>
public static class StaticFileCaching
{
    public const string Immutable = "public, max-age=31536000, immutable";
    public const string Revalidate = "no-cache";
    // The logos, icons and manifest have no version in their URLs; a changed one reaches visitors within a day.
    public const string OneDay = "public, max-age=86400";

    /// <param name="fileName">The file served, e.g. <c>app.js</c> (also <c>index.html</c> for <c>/</c>).</param>
    /// <param name="requestedVersion">The request's <c>v</c>, if it has one.</param>
    /// <param name="buildVersion">This build's version, or null for a local build: its files change without a new version.</param>
    public static string For(string fileName, string? requestedVersion, string? buildVersion)
    {
        if (fileName == "index.html")
            return Revalidate;
        if (requestedVersion is null)
            return OneDay;
        // Another version's URL gets today's file, which mustn't be kept under that name for a year.
        return buildVersion is not null && requestedVersion == buildVersion ? Immutable : Revalidate;
    }
}
