using System.Text;
using LazyDad.Api.Configuration;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class HtmlGeneratorService
{
    // The service is scoped, so the lock must be static to serialize writers across scopes
    // (concurrent language loops, startup regeneration).
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IJokeRepository jokeRepository;
    private readonly ITopJokeRepository topJokeRepository;
    private readonly IOptions<JokeGenerationOptions> options;
    private readonly IOptions<AppInfoOptions> appInfo;
    private readonly IWebHostEnvironment env;
    private readonly ILogger<HtmlGeneratorService> logger;

    public HtmlGeneratorService(
        IJokeRepository jokeRepository,
        ITopJokeRepository topJokeRepository,
        IOptions<JokeGenerationOptions> options,
        IOptions<AppInfoOptions> appInfo,
        IWebHostEnvironment env,
        ILogger<HtmlGeneratorService> logger)
    {
        this.jokeRepository = jokeRepository;
        this.topJokeRepository = topJokeRepository;
        this.options = options;
        this.appInfo = appInfo;
        this.env = env;
        this.logger = logger;
    }

    public async Task RegenerateAsync(CancellationToken cancellationToken)
    {
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            var jokes = await jokeRepository.GetAllAsync(cancellationToken);
            var topJokes = await topJokeRepository.GetAllAsync(cancellationToken);

            var html = BuildHtml(jokes, topJokes, LanguageCodes(options.Value), appInfo.Value);

            var wwwroot = Path.Combine(env.ContentRootPath, "wwwroot");
            Directory.CreateDirectory(wwwroot);

            // Write to a temp file and rename over the old page, so a request never sees a half-written file.
            var path = Path.Combine(wwwroot, "index.html");
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, html, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, path, overwrite: true);

            logger.LogInformation("index.html regenerated ({Count} jokes).", jokes.Count);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    internal static IReadOnlyDictionary<string, string> LanguageCodes(JokeGenerationOptions options)
        => options.Languages
            .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
            .ToDictionary(l => l.Language, l => l.LanguageCode, StringComparer.OrdinalIgnoreCase);

    internal static string BuildHtml(
        IReadOnlyList<Joke> jokes,
        IReadOnlyList<TopJoke> topJokes,
        IReadOnlyDictionary<string, string> languageCodes,
        AppInfoOptions appInfo)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>LazyDad</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: Georgia, serif; max-width: 700px; margin: 0 auto; padding: 2rem 1rem; background: #fafafa; color: #333; }");
        sb.AppendLine("    h1 { font-size: 2rem; margin-bottom: 0.25rem; }");
        sb.AppendLine("    .subtitle { color: #888; margin-bottom: 2rem; font-size: 0.9rem; }");
        sb.AppendLine("    .joke { background: #fff; border: 1px solid #e8e8e8; border-radius: 6px; padding: 1rem 1.2rem; margin: 0.75rem 0; }");
        sb.AppendLine("    .joke p { margin: 0 0 0.4rem; line-height: 1.5; }");
        sb.AppendLine("    .joke-meta { display: flex; gap: 0.5rem; align-items: center; margin-top: 0.4rem; flex-wrap: wrap; }");
        sb.AppendLine("    .joke time { color: #aaa; font-size: 0.8rem; font-style: italic; margin-right: 0.5rem; }");
        sb.AppendLine("    .tag { color: #fff; border-radius: 3px; font-size: 0.7rem; padding: 0.1rem 0.4rem; font-family: monospace; }");
        sb.AppendLine("    .tag-lang { background: #5a8a5a; }");
        sb.AppendLine("    .tag-model { background: #888; }");
        sb.AppendLine("    .empty { color: #aaa; font-style: italic; }");
        sb.AppendLine("    h2 { font-size: 1.2rem; margin: 2rem 0 0.5rem; }");
        sb.AppendLine("    .top .joke { border-color: #e0c060; background: #fffbea; }");
        sb.AppendLine("    .rank { font-size: 1.3rem; margin-right: 0.4rem; }");
        sb.AppendLine("    .reason { color: #8a7a40; font-size: 0.85rem; font-style: italic; }");
        sb.AppendLine("    .tag-judge { background: #b08a2a; }");
        sb.AppendLine("    footer { margin-top: 2.5rem; color: #aaa; font-size: 0.8rem; }");
        sb.AppendLine("    footer a { color: inherit; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>LazyDad</h1>");
        sb.AppendLine($"  <p class=\"subtitle\">{jokes.Count} joke{(jokes.Count == 1 ? "" : "s")} generated so far.</p>");

        foreach (var group in topJokes.GroupBy(t => t.Language))
        {
            sb.AppendLine("  <section class=\"top\">");
            sb.AppendLine($"    <h2>Top {group.Count()} &middot; {EscapeHtml(group.Key)}</h2>");
            foreach (var top in group.OrderBy(t => t.Rank))
            {
                sb.AppendLine("    <div class=\"joke\">");
                sb.AppendLine($"      <p><span class=\"rank\">{RankBadge(top.Rank)}</span><span{LangAttribute(top.Language, languageCodes)}>{EscapeHtml(top.Joke.Text)}</span></p>");
                if (!string.IsNullOrWhiteSpace(top.Reason))
                    sb.AppendLine($"      <p class=\"reason\">{EscapeHtml(top.Reason)}</p>");
                sb.AppendLine("      <div class=\"joke-meta\">");
                sb.AppendLine($"        <time datetime=\"{top.Joke.GeneratedAt:yyyy-MM-ddTHH:mm:ssZ}\">{top.Joke.GeneratedAt:dd MMM yyyy}</time>");
                if (!string.IsNullOrWhiteSpace(top.Joke.Model))
                    sb.AppendLine($"        <span class=\"tag tag-model\">{EscapeHtml(top.Joke.Model)}</span>");
                sb.AppendLine($"        <span class=\"tag tag-judge\">judged by {EscapeHtml(top.JudgeModel)}</span>");
                sb.AppendLine("      </div>");
                sb.AppendLine("    </div>");
            }
            sb.AppendLine("  </section>");
        }

        if (topJokes.Count > 0)
            sb.AppendLine("  <h2>All jokes</h2>");

        if (jokes.Count == 0)
        {
            sb.AppendLine("  <p class=\"empty\">No jokes yet. Check back soon.</p>");
        }
        else
        {
            foreach (var joke in jokes)
            {
                var iso = joke.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var display = joke.GeneratedAt.ToString("dd MMM yyyy");
                sb.AppendLine("  <div class=\"joke\">");
                sb.AppendLine($"    <p{LangAttribute(joke.Language, languageCodes)}>{EscapeHtml(joke.Text)}</p>");
                sb.AppendLine("    <div class=\"joke-meta\">");
                sb.AppendLine($"      <time datetime=\"{iso}\">{display}</time>");
                if (!string.IsNullOrWhiteSpace(joke.Language))
                    sb.AppendLine($"      <span class=\"tag tag-lang\">{EscapeHtml(joke.Language)}</span>");
                if (!string.IsNullOrWhiteSpace(joke.Model))
                    sb.AppendLine($"      <span class=\"tag tag-model\">{EscapeHtml(joke.Model)}</span>");
                sb.AppendLine("    </div>");
                sb.AppendLine("  </div>");
            }
        }

        sb.AppendLine($"  <footer>{VersionFooter(appInfo)}</footer>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string RankBadge(int rank) => rank switch
    {
        1 => "🥇",
        2 => "🥈",
        3 => "🥉",
        _ => $"#{rank}"
    };

    // The page chrome is English (<html lang="en">); each joke is tagged with its own language
    // so screen readers pronounce it correctly.
    // "Version 2026.09.25 · e33d99a" (CalVer from the commit date, then the short SHA linked to
    // the commit), or "Version dev (local build)" when the image wasn't built by the pipeline.
    internal static string VersionFooter(AppInfoOptions appInfo)
    {
        if (appInfo.CalendarVersion is null)
            return $"Version {EscapeHtml(appInfo.Version)} (local build)";

        var sha = EscapeHtml(appInfo.Version);
        var shaHtml = appInfo.CommitUrl is { } url ? $"<a href=\"{EscapeHtml(url)}\">{sha}</a>" : sha;
        return $"Version {appInfo.CalendarVersion} &middot; {shaHtml}";
    }

    private static string LangAttribute(string language, IReadOnlyDictionary<string, string> languageCodes)
        => languageCodes.TryGetValue(language, out var code) ? $" lang=\"{EscapeHtml(code)}\"" : string.Empty;

    private static string EscapeHtml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
