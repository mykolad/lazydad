using System.Text;
using System.Text.Json;
using LazyDad.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

/// <summary>
/// Writes <c>wwwroot/index.html</c>: the page shell (header, the version link, the loading state, and
/// the config for <c>app.js</c>). The jokes, the leaderboard and the votes are live data, which
/// <c>app.js</c> fetches from the API, so the shell only changes with the build and is written once at startup.
/// </summary>
public class HtmlGeneratorService
{
    // The service is scoped, so the lock must be static to serialize writers across scopes.
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    // Lucide icons (stroke 2.75), inlined so the shell renders them before app.js runs.
    private const string SvgOpen = "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.75\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">";
    private const string ClockIcon = SvgOpen + "<circle cx=\"12\" cy=\"12\" r=\"10\"/><polyline points=\"12 6 12 12 16 14\"/></svg>";
    private const string MonitorIcon = SvgOpen + "<rect width=\"20\" height=\"14\" x=\"2\" y=\"3\" rx=\"2\"/><path d=\"M8 21h8M12 17v4\"/></svg>";
    private const string SunIcon = SvgOpen + "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41\"/></svg>";
    private const string MoonIcon = SvgOpen + "<path d=\"M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z\"/></svg>";
    private const string CommitIcon = "<svg width=\"13\" height=\"13\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.75\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M3 12h6M15 12h6\"/></svg>";

    // Runs before first paint, so the page never flashes the wrong theme: the stored preference,
    // else the system's. localStorage can throw (private mode, blocked storage).
    private const string ThemeBootstrap =
        "(function(){var d=document.documentElement,t=null,l=null;" +
        "try{t=localStorage.getItem('lazydad.theme');l=localStorage.getItem('lazydad.lang')}catch(e){}" +
        "if(t!=='light'&&t!=='dark')t=window.matchMedia&&matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';" +
        "d.setAttribute('data-theme',t);if(l==='en')d.lang='en';" +
        // The browser chrome follows the page's theme, including a manual choice.
        "var m=document.getElementById('ld-theme-color');if(m)m.content=t==='dark'?'#1d1a16':'#f5ead8'})();";

    private readonly IOptions<JokeGenerationOptions> options;
    private readonly IOptions<AppInfoOptions> appInfo;
    private readonly IWebHostEnvironment env;
    private readonly ILogger<HtmlGeneratorService> logger;

    public HtmlGeneratorService(
        IOptions<JokeGenerationOptions> options,
        IOptions<AppInfoOptions> appInfo,
        IWebHostEnvironment env,
        ILogger<HtmlGeneratorService> logger)
    {
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
            var html = BuildHtml(LanguageCodes(options.Value), appInfo.Value);

            var wwwroot = Path.Combine(env.ContentRootPath, "wwwroot");
            Directory.CreateDirectory(wwwroot);

            // Write to a temp file and rename over the old page, so a request never sees a half-written file.
            var path = Path.Combine(wwwroot, "index.html");
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, html, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, path, overwrite: true);

            logger.LogInformation("index.html written (version {Version}).", appInfo.Value.Version);
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

    internal static string BuildHtml(IReadOnlyDictionary<string, string> languageCodes, AppInfoOptions appInfo)
    {
        // Busts browser caches of app.css/app.js on every deploy.
        var assetVersion = Uri.EscapeDataString(appInfo.Version);
        // app.js tags each joke with its language code, so screen readers pronounce it correctly.
        // System.Text.Json escapes <, > and &, so the JSON can't close the script element.
        var config = JsonSerializer.Serialize(new { languageCodes });

        // The shell's text is Ukrainian (the default); app.js switches [data-i18n] elements to English.
        return $$"""
            <!DOCTYPE html>
            <html lang="uk" data-theme="light">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LazyDad</title>
              <meta name="description" content="Українські батьківські жарти від ШІ: нова партія кожні 4 години, найкращі три обирає ШІ-суддя.">
              <meta name="color-scheme" content="light dark">
              <meta name="theme-color" content="#f5ead8" id="ld-theme-color">
              <link rel="icon" href="/favicon.ico" sizes="48x48">
              <link rel="icon" href="/favicon.svg" type="image/svg+xml">
              <link rel="apple-touch-icon" href="/apple-touch-icon.png">
              <link rel="manifest" href="/site.webmanifest">
              <script>{{ThemeBootstrap}}</script>
              <link rel="preconnect" href="https://fonts.googleapis.com">
              <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
              <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Caprasimo&family=Nunito:wght@400;600;700;900&subset=cyrillic,cyrillic-ext&display=swap">
              <link rel="stylesheet" href="app.css?v={{assetVersion}}">
              <script src="app.js?v={{assetVersion}}" defer></script>
            </head>
            <body>
            <div class="ld-root">
              <header class="ld-header">
                <div class="ld-brand">
                  <img class="ld-logo ld-logo--light" src="logo.svg" width="46" height="46" alt="">
                  <img class="ld-logo ld-logo--dark" src="logo-dark.svg" width="46" height="46" alt="">
                  <div class="ld-brand-text">
                    <h1 class="ld-wordmark">LazyDad</h1>
                    <span class="ld-count" id="ld-count"></span>
                  </div>
                </div>
                <div class="ld-next" id="ld-next" hidden>{{ClockIcon}}<span data-i18n="next">Нові жарти через</span><span class="ld-num" id="ld-countdown"></span></div>
                <div class="ld-controls">
                  <div class="ld-pills" role="group" aria-label="Мова" data-i18n-aria="langGroup">
                    <button type="button" data-set-lang="ua" aria-pressed="true">UA</button>
                    <button type="button" data-set-lang="en" aria-pressed="false">EN</button>
                  </div>
                  <div class="ld-pills" role="group" aria-label="Тема" data-i18n-aria="themeGroup">
                    <button type="button" class="ld-icon-btn" data-set-theme="system" aria-pressed="true" title="Як у системі" aria-label="Як у системі" data-i18n-title="themeSystem" data-i18n-aria="themeSystem">{{MonitorIcon}}</button>
                    <button type="button" class="ld-icon-btn" data-set-theme="light" aria-pressed="false" title="Світла тема" aria-label="Світла тема" data-i18n-title="themeLight" data-i18n-aria="themeLight">{{SunIcon}}</button>
                    <button type="button" class="ld-icon-btn" data-set-theme="dark" aria-pressed="false" title="Темна тема" aria-label="Темна тема" data-i18n-title="themeDark" data-i18n-aria="themeDark">{{MoonIcon}}</button>
                  </div>
                </div>
              </header>
              <main class="ld-main">
                <noscript><p>LazyDad needs JavaScript to show the jokes.</p></noscript>
                <div class="ld-state ld-loading" id="ld-loading" aria-busy="true">
                  <div class="ld-skel ld-skel--hero">
                    <div class="ld-bar" style="width:56px;height:56px"></div>
                    <div class="ld-bar" style="width:86%;height:22px"></div>
                    <div class="ld-bar" style="width:60%;height:22px"></div>
                    <div class="ld-bar ld-bar--soft" style="width:40%;height:12px;margin-top:8px"></div>
                  </div>
                  {{Skeleton("92%")}}
                  {{Skeleton("78%")}}
                  {{Skeleton("86%")}}
                  <span class="ld-muted" id="ld-loading-text" role="status" aria-live="polite" style="font-size:13px"><span data-i18n="loading">Завантажуємо жарти…</span></span>
                </div>
                <section class="ld-state ld-empty" id="ld-empty" hidden>
                  <div class="ld-empty-blob" aria-hidden="true">z z</div>
                  <h2 data-i18n="emptyT">Тато ще прокидається</h2>
                  <p id="ld-empty-text"></p>
                </section>
                <div class="ld-aside" id="ld-aside">
                  <section class="ld-panel" data-theme="dark" id="ld-spotlight" aria-label="Топ-3 від ШІ-судді" data-i18n-aria="top" hidden></section>
                  <div class="ld-toplist" id="ld-toplist"></div>
                  {{VersionHtml(appInfo)}}
                </div>
                <section class="ld-feed" id="ld-feed" aria-label="Усі жарти" data-i18n-aria="all" hidden>
                  <div class="ld-feed-head">
                    <h2 data-i18n="all">Усі жарти</h2>
                    <div class="ld-seg" role="radiogroup" aria-label="Порядок" data-i18n-aria="sort">
                      <label class="ld-seg-opt"><input type="radio" name="ld-sort" value="new" checked><span data-i18n="newest">Нові</span></label>
                      <label class="ld-seg-opt"><input type="radio" name="ld-sort" value="top"><span data-i18n="best">Найкращі</span></label>
                    </div>
                  </div>
                  <div class="ld-list" id="ld-list"></div>
                  <div class="ld-sentinel" id="ld-sentinel"></div>
                  <div class="ld-more" id="ld-more" hidden><span class="ld-dot"></span><span class="ld-dot"></span><span class="ld-dot"></span><span data-i18n="more">Шукаємо ще жарти…</span></div>
                  <p class="ld-end" id="ld-end" hidden data-i18n="end">Це всі жарти. Поки що.</p>
                </section>
              </main>
            </div>
            <script type="application/json" id="ld-config">{{config}}</script>
            </body>
            </html>

            """;
    }

    private static string Skeleton(string width)
        => $"<div class=\"ld-skel\"><div class=\"ld-bar\" style=\"width:{width}\"></div><div class=\"ld-bar\" style=\"width:45%\"></div>" +
           "<div class=\"ld-skel-row\"><div class=\"ld-bar ld-bar--soft\" style=\"width:120px;height:10px\"></div><div class=\"ld-bar ld-bar--soft\" style=\"width:112px;height:34px\"></div></div></div>";

    // "v2026.09.25 e33d99a" (CalVer from the commit date, then the short SHA), linked to the commit.
    // A build outside the pipeline has no commit date: "vdev (local build)".
    internal static string VersionHtml(AppInfoOptions appInfo)
    {
        const string attributes = "class=\"ld-version\" title=\"Версія збірки\" data-i18n-title=\"build\"";
        if (appInfo.CalendarVersion is null)
            return $"<span {attributes}>{CommitIcon}<span>v{EscapeHtml(appInfo.Version)} (local build)</span></span>";

        var content = $"{CommitIcon}<span>v{appInfo.CalendarVersion}</span><span class=\"ld-sha\">{EscapeHtml(appInfo.Version)}</span>";
        return appInfo.CommitUrl is { } url
            ? $"<a {attributes} href=\"{EscapeHtml(url)}\" target=\"_blank\" rel=\"noopener\">{content}</a>"
            : $"<span {attributes}>{content}</span>";
    }

    private static string EscapeHtml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
