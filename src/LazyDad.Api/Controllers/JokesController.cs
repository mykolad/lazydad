using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace LazyDad.Api.Controllers;

[ApiController]
[Route("jokes")]
public class JokesController : ControllerBase
{
    private readonly IJokeRepository jokeRepository;

    public JokesController(IJokeRepository jokeRepository)
    {
        this.jokeRepository = jokeRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var jokes = await jokeRepository.GetAllAsync(cancellationToken);
        return Ok(jokes);
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
