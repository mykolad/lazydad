using LazyDad.Data.Entities;

namespace LazyDad.Data.Repositories;

public interface IJokeRepository
{
    Task<List<Joke>> GetAllAsync(CancellationToken cancellationToken);
    Task<List<Joke>> GetByLanguageAsync(string language, CancellationToken cancellationToken);
    /// <summary>Returns the N most recent jokes for a language, newest first. Used to build the uniqueness prompt.</summary>
    Task<List<Joke>> GetRecentByLanguageAsync(string language, int count, CancellationToken cancellationToken);
    Task<Joke?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task AddAsync(Joke joke, CancellationToken cancellationToken);
}
