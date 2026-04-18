using System.Text;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;

namespace LazyDad.Api.Services;

public class HtmlGeneratorService
{
    private readonly IJokeRepository jokeRepository;
    private readonly IWebHostEnvironment env;
    private readonly ILogger<HtmlGeneratorService> logger;

    public HtmlGeneratorService(
        IJokeRepository jokeRepository,
        IWebHostEnvironment env,
        ILogger<HtmlGeneratorService> logger)
    {
        this.jokeRepository = jokeRepository;
        this.env = env;
        this.logger = logger;
    }

    public async Task RegenerateAsync(CancellationToken cancellationToken)
    {
        var jokes = await jokeRepository.GetAllAsync(cancellationToken);

        var html = BuildHtml(jokes);

        var wwwroot = Path.Combine(env.ContentRootPath, "wwwroot");
        Directory.CreateDirectory(wwwroot);

        var path = Path.Combine(wwwroot, "index.html");
        await File.WriteAllTextAsync(path, html, Encoding.UTF8, cancellationToken);

        logger.LogInformation("index.html regenerated ({Count} jokes).", jokes.Count);
    }

    private static string BuildHtml(IReadOnlyList<Joke> jokes)
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
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <h1>LazyDad</h1>");
        sb.AppendLine($"  <p class=\"subtitle\">{jokes.Count} joke{(jokes.Count == 1 ? "" : "s")} generated so far.</p>");

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
                sb.AppendLine($"    <p>{EscapeHtml(joke.Text)}</p>");
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

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string EscapeHtml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
