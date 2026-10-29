using LazyDad.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LazyDad.Api.Controllers;

/// <summary>The privacy page, <c>/privacy</c>: the page shell with the policy already in it (see <see cref="PrivacyPolicy"/>).</summary>
[ApiController]
public class PrivacyPageController : ControllerBase
{
    private readonly HtmlGeneratorService html;

    public PrivacyPageController(HtmlGeneratorService html)
    {
        this.html = html;
    }

    [HttpGet("privacy")]
    public ContentResult Get()
        => new() { Content = html.PrivacyHtml($"{Request.Scheme}://{Request.Host}"), ContentType = "text/html; charset=utf-8", StatusCode = StatusCodes.Status200OK };
}
