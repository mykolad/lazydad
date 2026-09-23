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

        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Ukrainian", "Жарт")], codes);

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("<p lang=\"uk\">Жарт</p>", html);
    }

    [Fact]
    public void BuildHtml_WithoutLanguageCode_OmitsLangAttribute()
    {
        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Klingon", "Joke")], new Dictionary<string, string>());

        Assert.Contains("<p>Joke</p>", html);
    }

    [Fact]
    public void BuildHtml_EscapesJokeText()
    {
        var html = HtmlGeneratorService.BuildHtml([MakeJoke("Ukrainian", "<script>alert(\"x\")</script> & co")], new Dictionary<string, string>());

        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void BuildHtml_WithNoJokes_ShowsEmptyMessage()
    {
        var html = HtmlGeneratorService.BuildHtml([], new Dictionary<string, string>());

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
}
