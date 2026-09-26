using System.Text.Json;
using System.Text.RegularExpressions;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class HtmlGeneratorServiceTests
{
    private static readonly AppInfoOptions PipelineBuild = new()
    {
        Version = "e33d99a",
        Revision = "e33d99a1234567890abcdef1234567890abcdef",
        CommitDate = new DateTimeOffset(2026, 9, 25, 21, 5, 0, TimeSpan.FromHours(3)),
        SourceUrl = "https://github.com/mykolad/lazydad/"
    };

    private static string Build(AppInfoOptions appInfo)
        => HtmlGeneratorService.BuildHtml(new Dictionary<string, string> { ["Ukrainian"] = "uk" }, appInfo);

    [Fact]
    public void BuildHtml_IsTheShellForAppJs_WithVersionedAssets()
    {
        var html = Build(PipelineBuild);

        Assert.Contains("<title>LazyDad</title>", html);
        Assert.Contains("<html lang=\"uk\" data-theme=\"light\">", html);
        Assert.Contains("<link rel=\"stylesheet\" href=\"app.css?v=e33d99a\">", html);
        Assert.Contains("<script src=\"app.js?v=e33d99a\" defer></script>", html);
        // Every element app.js looks up by id is in the shell.
        foreach (var id in new[] { "ld-count", "ld-next", "ld-countdown", "ld-loading", "ld-loading-text", "ld-empty", "ld-empty-text",
                     "ld-aside", "ld-spotlight", "ld-toplist", "ld-feed", "ld-list", "ld-sentinel", "ld-more", "ld-end", "ld-config" })
            Assert.Contains($"id=\"{id}\"", html);
        // app.js replaces its text with the retryable error: a live region, so that's announced.
        Assert.Contains("id=\"ld-loading-text\" role=\"status\" aria-live=\"polite\"", html);
    }

    [Fact]
    public void BuildHtml_SetsTheThemeBeforeFirstPaint()
    {
        var html = Build(PipelineBuild);

        // The bootstrap script comes before the stylesheet, so the first paint already has the theme.
        var bootstrap = html.IndexOf("localStorage.getItem('lazydad.theme')", StringComparison.Ordinal);
        Assert.True(bootstrap >= 0 && bootstrap < html.IndexOf("app.css", StringComparison.Ordinal));
        Assert.Contains("prefers-color-scheme: dark", html);
    }

    [Fact]
    public void BuildHtml_EmbedsTheLanguageCodesAsJson()
    {
        var html = Build(PipelineBuild);

        var json = Regex.Match(html, "<script type=\"application/json\" id=\"ld-config\">(.*?)</script>").Groups[1].Value;
        using var config = JsonDocument.Parse(json);
        Assert.Equal("uk", config.RootElement.GetProperty("languageCodes").GetProperty("Ukrainian").GetString());
    }

    [Fact]
    public void BuildHtml_ConfigJson_CannotCloseTheScriptElement()
    {
        var html = HtmlGeneratorService.BuildHtml(new Dictionary<string, string> { ["</script><b>"] = "x" }, PipelineBuild);

        Assert.DoesNotContain("</script><b>", html);
    }

    [Fact]
    public void BuildHtml_ShowsTheVersionUnderTheTopThree()
    {
        var html = Build(PipelineBuild);

        var panel = html.IndexOf("id=\"ld-spotlight\"", StringComparison.Ordinal);
        var version = html.IndexOf("class=\"ld-version\"", StringComparison.Ordinal);
        Assert.True(panel >= 0 && version > panel, "The version link should follow the Top 3 panel in the aside.");
    }

    [Fact]
    public void LanguageCodes_MapsConfiguredLanguagesCaseInsensitively_AndSkipsBlankCodes()
    {
        var options = new JokeGenerationOptions
        {
            Languages =
            [
                new() { Language = "Ukrainian", LanguageCode = "uk" },
                new() { Language = "Klingon", LanguageCode = "" }
            ]
        };

        var codes = HtmlGeneratorService.LanguageCodes(options);

        Assert.Equal("uk", codes["ukrainian"]);
        Assert.False(codes.ContainsKey("Klingon"));
    }

    [Fact]
    public void VersionHtml_ForPipelineBuild_ShowsCalVerAndShortSha_LinkedToTheCommit()
    {
        var html = HtmlGeneratorService.VersionHtml(PipelineBuild);

        Assert.StartsWith("<a class=\"ld-version\"", html);
        Assert.Contains("href=\"https://github.com/mykolad/lazydad/commit/e33d99a1234567890abcdef1234567890abcdef\"", html);
        Assert.Contains("<span>v2026.09.25</span><span class=\"ld-sha\">e33d99a</span>", html);
    }

    [Fact]
    public void VersionHtml_UsesTheUtcDate()
    {
        // 00:30 on the 26th in UTC+3 is still the 25th in UTC.
        var appInfo = new AppInfoOptions { Version = "abc1234", CommitDate = new DateTimeOffset(2026, 9, 26, 0, 30, 0, TimeSpan.FromHours(3)) };

        Assert.Contains("<span>v2026.09.25</span>", HtmlGeneratorService.VersionHtml(appInfo));
    }

    [Fact]
    public void VersionHtml_WithoutRepository_ShowsShaWithoutLink()
    {
        var appInfo = new AppInfoOptions { Version = "abc1234", CommitDate = DateTimeOffset.UtcNow };

        var html = HtmlGeneratorService.VersionHtml(appInfo);

        Assert.DoesNotContain("<a ", html);
        Assert.Contains("<span class=\"ld-sha\">abc1234</span>", html);
    }

    [Fact]
    public void VersionHtml_ForLocalBuild_SaysSo()
        => Assert.Contains("<span>vdev (local build)</span>", HtmlGeneratorService.VersionHtml(new AppInfoOptions()));

    [Fact]
    public void VersionHtml_EscapesTheVersion()
        => Assert.Contains("v&lt;x&gt; (local build)", HtmlGeneratorService.VersionHtml(new AppInfoOptions { Version = "<x>" }));

    [Fact]
    public async Task RegenerateAsync_WritesIndexHtmlUnderWwwroot()
    {
        var contentRoot = Directory.CreateTempSubdirectory("lazydad-tests-").FullName;
        try
        {
            var env = new Mock<IWebHostEnvironment>();
            env.Setup(e => e.ContentRootPath).Returns(contentRoot);
            var service = new HtmlGeneratorService(
                Options.Create(new JokeGenerationOptions { Languages = [new() { Language = "Ukrainian", LanguageCode = "uk" }] }),
                Options.Create(PipelineBuild),
                env.Object,
                NullLogger<HtmlGeneratorService>.Instance);

            await service.RegenerateAsync(CancellationToken.None);

            var html = await File.ReadAllTextAsync(Path.Combine(contentRoot, "wwwroot", "index.html"));
            Assert.Contains("<span class=\"ld-sha\">e33d99a</span>", html);
            Assert.False(File.Exists(Path.Combine(contentRoot, "wwwroot", "index.html.tmp")));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
