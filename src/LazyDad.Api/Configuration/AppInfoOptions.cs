namespace LazyDad.Api.Configuration;

/// <summary>
/// Build metadata, baked into the image by the Dockerfile (App__Version etc.) from what
/// Deploy Master passes as build arguments. Local runs keep the defaults.
/// </summary>
public class AppInfoOptions
{
    public const string SectionName = "App";

    /// <summary>Short commit SHA of the build, or "dev" for local builds.</summary>
    public string Version { get; set; } = "dev";

    /// <summary>Full commit SHA, used for the commit link.</summary>
    public string Revision { get; set; } = string.Empty;

    /// <summary>Commit timestamp; the date part is the human-readable CalVer (yyyy.MM.dd).</summary>
    public DateTimeOffset? CommitDate { get; set; }

    /// <summary>Repository URL, e.g. https://github.com/owner/repo.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>CalVer from the commit date, e.g. 2026.09.25; null for local builds.</summary>
    public string? CalendarVersion => CommitDate?.UtcDateTime.ToString("yyyy.MM.dd");

    /// <summary>Link to the commit, when both the repository and the full SHA are known.</summary>
    public string? CommitUrl => string.IsNullOrWhiteSpace(SourceUrl) || string.IsNullOrWhiteSpace(Revision)
        ? null
        : $"{SourceUrl.TrimEnd('/')}/commit/{Revision}";
}
