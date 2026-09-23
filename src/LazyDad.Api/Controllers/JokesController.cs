using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace LazyDad.Api.Controllers;

[ApiController]
[Route("jokes")]
public class JokesController : ControllerBase
{
    private readonly IJokeRepository jokeRepository;
    private readonly ITopJokeRepository topJokeRepository;

    public JokesController(IJokeRepository jokeRepository, ITopJokeRepository topJokeRepository)
    {
        this.jokeRepository = jokeRepository;
        this.topJokeRepository = topJokeRepository;
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
