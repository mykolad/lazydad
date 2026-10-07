using System.Text.RegularExpressions;
using LazyDad.Api.Configuration;
using LazyDad.Api.Controllers;
using LazyDad.Api.Services;
using LazyDad.Api.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class PrivacyPageControllerTests
{
    private static ContentResult Get()
    {
        var html = new HtmlGeneratorService(
            Options.Create(new JokeGenerationOptions { Languages = [new() { Language = "Ukrainian", LanguageCode = "uk" }] }),
            Options.Create(new AppInfoOptions { Version = "e33d99a" }),
            new EnabledSignInProviders(["github"]),
            Mock.Of<IWebHostEnvironment>(),
            NullLogger<HtmlGeneratorService>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("lazydad.fyi");
        return new PrivacyPageController(html) { ControllerContext = new ControllerContext { HttpContext = context } }.Get();
    }

    [Fact]
    public void Get_ServesThePolicyInTheHtml_InBothLanguages()
    {
        // Providers check the privacy URL with crawlers that don't run app.js.
        var result = Get();

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        var html = result.Content!;
        Assert.Contains("<title>Конфіденційність — LazyDad</title>", html);
        Assert.Contains("<link rel=\"canonical\" href=\"https://lazydad.fyi/privacy\">", html);
        Assert.Matches("<div class=\"ld-doc-body\" lang=\"uk\">\\s*<h2>Конфіденційність</h2>", html);
        Assert.Matches("<div class=\"ld-doc-body\" lang=\"en\">\\s*<h2>Privacy</h2>", html);
        Assert.Contains("__Host-lazydad", html);
        Assert.Contains("https://github.com/mykolad", html);
    }

    [Fact]
    public void Get_ShowsThePolicyInsteadOfTheJokesLoading()
    {
        var html = Get().Content!;

        Assert.Matches("id=\"ld-loading\" aria-busy=\"true\" hidden", html);
        Assert.Matches("id=\"ld-aside\" hidden", html);
        Assert.DoesNotContain("<noscript>", html);
    }

    [Fact]
    public void EveryPage_LinksToThePolicy()
    {
        var site = HtmlGeneratorService.BuildHtml(new Dictionary<string, string>(), [], new AppInfoOptions { Version = "e33d99a" },
            HtmlGeneratorService.SitePage);

        Assert.Matches(new Regex("<footer class=\"ld-footer\"><a href=\"/privacy\"[^>]*>"), site);
        Assert.Contains("<noscript>", site);
    }
}
