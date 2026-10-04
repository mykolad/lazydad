using LazyDad.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LazyDad.Api.Controllers;

/// <summary>
/// A joke's own page, <c>/j/&lt;id&gt;</c>: what the share buttons link to. It's the page shell with the joke in the
/// title and the link-preview tags, which messengers read without running <c>app.js</c>; <c>app.js</c> then renders
/// the joke from the API. An unknown joke gets the plain shell with a 404, and <c>app.js</c> says it isn't there.
/// </summary>
[ApiController]
[Route("j")]
public class JokePageController : ControllerBase
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    private readonly JokeReadCache cache;
    private readonly HtmlGeneratorService html;

    public JokePageController(JokeReadCache cache, HtmlGeneratorService html)
    {
        this.cache = cache;
        this.html = html;
    }

    [HttpGet("{id:int}")]
    public async Task<ContentResult> Get(int id, CancellationToken cancellationToken)
    {
        var joke = await JokesController.GetJokeCached(cache, id, cancellationToken);
        if (joke is null)
            return new ContentResult { Content = html.SiteHtml(), ContentType = HtmlContentType, StatusCode = StatusCodes.Status404NotFound };

        // The address the visitor used (the forwarded scheme and host: lazydad.fyi in production), so the preview links back to it.
        var origin = $"{Request.Scheme}://{Request.Host}";
        return new ContentResult { Content = html.JokePageHtml(joke, origin), ContentType = HtmlContentType, StatusCode = StatusCodes.Status200OK };
    }
}
