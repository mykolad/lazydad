using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Controllers;

[ApiController]
[Route("jokes")]
public class JokesController : ControllerBase
{
    public const int MaxPageSize = 50;
    public const string VotePolicy = "votes";
    public const int MaxSimilar = 12;

    private readonly IJokeRepository jokeRepository;
    private readonly SchedulerStatus schedulerStatus;
    private readonly JokeReadCache cache;
    private readonly IOptions<SimilarityOptions> similarityOptions;
    private readonly SimilarityMetrics similarityMetrics;

    public JokesController(
        IJokeRepository jokeRepository,
        SchedulerStatus schedulerStatus,
        JokeReadCache cache,
        IOptions<SimilarityOptions> similarityOptions,
        SimilarityMetrics similarityMetrics)
    {
        this.jokeRepository = jokeRepository;
        this.schedulerStatus = schedulerStatus;
        this.cache = cache;
        this.similarityOptions = similarityOptions;
        this.similarityMetrics = similarityMetrics;
    }

    // The cached reads resolve their repository from the cache's own scope (see JokeReadCache), not this request's.
    private static Task<int> CountJokes(IServiceProvider services, CancellationToken cancellationToken)
        => services.GetRequiredService<IJokeRepository>().CountAsync(cancellationToken);

    // A joke's page reads its joke twice (/j/<id> for the link preview, then /jokes/<id>): one load serves both.
    internal static Task<Joke?> GetJokeCached(JokeReadCache cache, int id, CancellationToken cancellationToken)
        => cache.GetOrLoadAsync($"joke:{id}",
            (services, token) => services.GetRequiredService<IJokeRepository>().GetByIdAsync(id, token), cancellationToken);

    [HttpGet("top")]
    public async Task<IActionResult> GetTop(CancellationToken cancellationToken)
    {
        var top = await cache.GetOrLoadAsync("top",
            (services, token) => services.GetRequiredService<ITopJokeRepository>().GetAllAsync(token), cancellationToken);
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

    /// <summary>
    /// One page of the page's "All jokes" list: <c>sort</c> is <c>new</c> or <c>top</c> (net score).
    /// Omit <c>after</c> for the first page, then pass the previous page's <c>next</c> (null on the last page).
    /// </summary>
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string sort,
        [FromQuery] string? after,
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
        if (limit < 1 || limit > MaxPageSize)
            return BadRequest($"limit must be between 1 and {MaxPageSize}.");
        JokeCursor? cursor = null;
        if (after is not null && !JokeCursor.TryParse(after, out cursor))
            return BadRequest("after must be a 'next' value from a previous page.");

        var total = await cache.GetOrLoadAsync("count", CountJokes, cancellationToken);
        // One extra row says whether another page exists, so the last page has no "next" even
        // when it's exactly full. Every visitor scrolling the same list asks for the same pages.
        var rows = await cache.GetOrLoadAsync($"feed:{sort}:{after}:{limit}",
            (services, token) => services.GetRequiredService<IJokeRepository>().GetPageAsync(order.Value, cursor, limit + 1, token),
            cancellationToken);
        var items = rows.Take(limit).ToList();
        var next = rows.Count > limit ? JokeCursor.After(items[^1]).ToString() : null;
        return Ok(new { total, items, next });
    }

    /// <summary>The page header: how many jokes exist, and when this revision's scheduler runs next (UTC, or null before it's scheduled).</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var count = await cache.GetOrLoadAsync("count", CountJokes, cancellationToken);
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
        var joke = await GetJokeCached(cache, id, cancellationToken);
        return joke is null ? NotFound() : Ok(joke);
    }

    /// <summary>"You might also like" on a joke's page: up to <c>limit</c> jokes like this one, most similar first (see JokeSimilarity).</summary>
    [HttpGet("{id:int}/similar")]
    public async Task<IActionResult> GetSimilar(int id, [FromQuery] int limit, CancellationToken cancellationToken)
    {
        if (limit < 1 || limit > MaxSimilar)
            return BadRequest($"limit must be between 1 and {MaxSimilar}.");
        var joke = await GetJokeCached(cache, id, cancellationToken);
        if (joke is null)
            return NotFound();
        // Every joke with its stems and stored profiles, loaded once per cache lifetime: every joke's page compares
        // against the same list, and nothing is called per request.
        var index = await cache.GetOrLoadAsync("similarity", LoadSimilarityIndex, cancellationToken);
        var result = JokeSimilarity.Similar(joke, index, limit);
        similarityMetrics.RecordSimilar(result.Method);
        return Ok(result.Jokes);
    }

    private async Task<List<JokeSimilarity.Entry>> LoadSimilarityIndex(IServiceProvider services, CancellationToken cancellationToken)
    {
        var jokes = await services.GetRequiredService<IJokeRepository>().GetAllAsync(cancellationToken);
        var profiles = services.GetRequiredService<IJokeProfileRepository>();
        var settings = similarityOptions.Value;
        // A method that's off (no Jev key, no embedding deployment) isn't used for ranking either, even with profiles stored.
        var jev = settings.Jev.Enabled
            ? await profiles.GetAllAsync(JokeProfile.JevKind, JokeProfiler.JevVersion(settings), cancellationToken)
            : [];
        var embeddings = settings.Embeddings.Enabled
            ? await profiles.GetAllAsync(JokeProfile.EmbeddingKind, JokeProfiler.EmbeddingVersion(settings), cancellationToken)
            : [];
        return JokeSimilarity.Index(jokes,
            jev.ToDictionary(p => p.JokeId, p => JokeProfiler.FromBytes(p.Vector)),
            embeddings.ToDictionary(p => p.JokeId, p => JokeProfiler.FromBytes(p.Vector)));
    }
}

/// <param name="Value">The vote now: 1 (funny), -1 (not funny) or 0 (none).</param>
/// <param name="Previous">This browser's vote before, as it remembers it.</param>
public sealed record VoteRequest(int Value, int Previous);
