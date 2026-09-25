using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data.Entities;

namespace LazyDad.Tests;

public class HtmlGeneratorServiceTests
{
    private static Joke MakeJoke(string language, string text)
        => new() { Id = 1, Language = language, Model = "gpt-5.3-chat", Text = text, GeneratedAt = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void BuildHtml_TagsJokeWithConfiguredLanguageCode()
    {
        var codes = new Dictionary<string, string> { ["Ukrainian"] = "uk" };

        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Ukrainian", "Жарт")], [], codes, new AppInfoOptions());

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<p lang=\"uk\">Жарт</p>", html);
    }

    [Fact]
    public void BuildHtml_WithoutLanguageCode_OmitsLangAttribute()
    {
        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Klingon", "Joke")], [], new Dictionary<string, string>(), new AppInfoOptions());

        Assert.Contains("<p>Joke</p>", html);
    }

    [Fact]
    public void BuildHtml_EscapesJokeText()
    {
        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Ukrainian", "<script>alert(\"x\")</script> & co")], [], new Dictionary<string, string>(), new AppInfoOptions());

        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void BuildHtml_WithNoJokes_ShowsEmptyMessage()
    {
        var html = HtmlGeneratorService.BuildHtml([], [], new Dictionary<string, string>(), new AppInfoOptions());

        Assert.Contains("No jokes yet", html);
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
    public void BuildHtml_RendersTopSectionWithMedalReasonAndLanguage()
    {
        var joke = MakeJoke("Ukrainian", "Найкращий жарт");
        var top = new TopJoke { Language = "Ukrainian", Rank = 1, JokeId = joke.Id, Joke = joke, Reason = "Clever <pun>", JudgeModel = "gpt-6-sol" };
        var codes = new Dictionary<string, string> { ["Ukrainian"] = "uk" };

        var html = HtmlGeneratorService.BuildHtml([joke], [top], codes, new AppInfoOptions());

        Assert.Contains("<h2>Top 1 &middot; Ukrainian</h2>", html);
        Assert.Contains("🥇</span><span lang=\"uk\">Найкращий жарт</span>", html);
        Assert.Contains("Clever &lt;pun&gt;", html);
        Assert.Contains("judged by gpt-6-sol", html);
        Assert.Contains("<h2>All jokes</h2>", html);
    }

    [Fact]
    public void VersionFooter_ForPipelineBuild_ShowsCalVerAndLinkedShortSha()
    {
        var appInfo = new AppInfoOptions
        {
            Version = "e33d99a",
            Revision = "e33d99a1234567890abcdef1234567890abcdef",
            CommitDate = new DateTimeOffset(2026, 9, 25, 21, 5, 0, TimeSpan.FromHours(3)),
            SourceUrl = "https://github.com/mykolad/lazydad/"
        };

        var footer = HtmlGeneratorService.VersionFooter(appInfo);

        Assert.Equal(
            "Version 2026.09.25 &middot; <a href=\"https://github.com/mykolad/lazydad/commit/e33d99a1234567890abcdef1234567890abcdef\">e33d99a</a>",
            footer);
    }

    [Fact]
    public void VersionFooter_UsesTheUtcDate()
    {
        // 00:30 on the 26th in UTC+3 is still the 25th in UTC.
        var appInfo = new AppInfoOptions { Version = "abc1234", CommitDate = new DateTimeOffset(2026, 9, 26, 0, 30, 0, TimeSpan.FromHours(3)) };

        Assert.StartsWith("Version 2026.09.25 &middot; abc1234", HtmlGeneratorService.VersionFooter(appInfo));
    }

    [Fact]
    public void VersionFooter_WithoutRepository_ShowsShaWithoutLink()
    {
        var appInfo = new AppInfoOptions { Version = "abc1234", CommitDate = DateTimeOffset.UtcNow };

        Assert.DoesNotContain("<a ", HtmlGeneratorService.VersionFooter(appInfo));
    }

    [Fact]
    public void VersionFooter_ForLocalBuild_SaysSo()
        => Assert.Equal("Version dev (local build)", HtmlGeneratorService.VersionFooter(new AppInfoOptions()));

    [Fact]
    public void BuildHtml_RendersTheVersionFooter()
    {
        var html = HtmlGeneratorService.BuildHtml([], [], new Dictionary<string, string>(), new AppInfoOptions());

        Assert.Contains("<footer>Version dev (local build)</footer>", html);
    }
}
