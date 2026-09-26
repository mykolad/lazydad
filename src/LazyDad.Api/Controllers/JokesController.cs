using LazyDad.Api.Services;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LazyDad.Api.Controllers;

[ApiController]
[Route("jokes")]
public class JokesController : ControllerBase
{
    public const int MaxPageSize = 50;
    public const string VotePolicy = "votes";

    private readonly IJokeRepository jokeRepository;
    private readonly ITopJokeRepository topJokeRepository;
    private readonly SchedulerStatus schedulerStatus;

    public JokesController(IJokeRepository jokeRepository, ITopJokeRepository topJokeRepository, SchedulerStatus schedulerStatus)
    {
        this.jokeRepository = jokeRepository;
        this.topJokeRepository = topJokeRepository;
        this.schedulerStatus = schedulerStatus;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var jokes = await jokeRepository.GetAllAsync(cancellationToken);
        return Ok(jokes);
    }

    [HttpGet("top")]
    public async Task<IActionResult> GetTop(CancellationToken cancellationToken)
    {
        var top = await topJokeRepository.GetAllAsync(cancellationToken);
        return Ok(top.Select(t => new
        {
            t.Language,
            t.Rank,
            t.Reason,
            t.JudgeModel,
            t.SelectedAt,
            t.Joke
        }));
    }

    /// <summary>One page of the page's "All jokes" list: <c>sort</c> is <c>new</c> or <c>top</c> (net score).</summary>
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string sort,
        [FromQuery] int offset,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        JokeSort? order = sort switch
        {
            "new" => JokeSort.Newest,
            "top" => JokeSort.TopVoted,
            _ => null
        };
        if (order is null)
            return BadRequest("sort must be 'new' or 'top'.");
        if (offset < 0 || limit < 1 || limit > MaxPageSize)
            return BadRequest($"offset must be at least 0 and limit between 1 and {MaxPageSize}.");

        var total = await jokeRepository.CountAsync(cancellationToken);
        var items = await jokeRepository.GetPageAsync(order.Value, offset, limit, cancellationToken);
        return Ok(new { total, items });
    }

    /// <summary>The page header: how many jokes exist, and when this revision's scheduler runs next (UTC, or null before it's scheduled).</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var count = await jokeRepository.CountAsync(cancellationToken);
        return Ok(new { count, nextBatchAt = schedulerStatus.NextTickAt });
    }

    /// <summary>
    /// Anonymous vote: the browser keeps its own vote (localStorage) and sends it as <c>previous</c>,
    /// so switching or removing a vote adjusts the counts. Returns the joke's new counts.
    /// </summary>
    [HttpPost("{id:int}/vote")]
    [EnableRateLimiting(VotePolicy)]
    public async Task<IActionResult> Vote(int id, [FromBody] VoteRequest request, CancellationToken cancellationToken)
    {
        if (!IsVote(request.Value) || !IsVote(request.Previous))
            return BadRequest("value and previous must be -1, 0 or 1.");

        var upDelta = (request.Value == 1 ? 1 : 0) - (request.Previous == 1 ? 1 : 0);
        var downDelta = (request.Value == -1 ? 1 : 0) - (request.Previous == -1 ? 1 : 0);

        var joke = upDelta == 0 && downDelta == 0
            ? await jokeRepository.GetByIdAsync(id, cancellationToken)
            : await jokeRepository.AddVotesAsync(id, upDelta, downDelta, cancellationToken);

        return joke is null ? NotFound() : Ok(new { up = joke.Up, down = joke.Down });

        static bool IsVote(int value) => value is -1 or 0 or 1;
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var joke = await jokeRepository.GetByIdAsync(id, cancellationToken);
        return joke is null ? NotFound() : Ok(joke);
    }

    [HttpGet("language/{language}")]
    public async Task<IActionResult> GetByLanguage(string language, CancellationToken cancellationToken)
    {
        var jokes = await jokeRepository.GetByLanguageAsync(language, cancellationToken);
        return Ok(jokes);
    }
}

/// <param name="Value">The vote now: 1 (funny), -1 (not funny) or 0 (none).</param>
/// <param name="Previous">This browser's vote before, as it remembers it.</param>
public sealed record VoteRequest(int Value, int Previous);
