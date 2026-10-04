using System.Buffers.Binary;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public interface IJokeProfiler
{
    /// <summary>Profiles up to <see cref="SimilarityOptions.BatchSize"/> jokes that lack a profile; returns how many profiles it saved.</summary>
    Task<int> ProfileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Gives jokes their profiles for "you might also like", once each: Jev's (the main method) and an embedding (the
/// fallback, for when Jev can't answer, e.g. out of credits). It takes the newest jokes without a profile, so a tick's
/// own jokes come first and the older ones follow, a batch per tick, until every joke has both. A profile is never
/// asked for again unless its version changes (a new pinned Jev model or question set, another embedding model).
/// A method that fails stops for this batch; the next tick tries again. Nothing here fails the tick (the scheduler
/// catches it anyway).
/// One replica at a time: a lease of its own in SchedulerLocks (<see cref="LeaseKey"/>), since a new revision's startup
/// tick can run while the old revision's tick is still profiling, and both would pay Jev for the same jokes.
/// </summary>
public sealed class JokeProfiler : IJokeProfiler
{
    public const string LeaseKey = "profiles";
    // Far longer than a batch takes (a few hundred answers of well under a second). Not released early: the next
    // profiling is the next tick, hours later; a startup tick within the half hour leaves its jokes to that one.
    public static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(30);
    private const int EmbeddingBatch = 100;
    // This process, as the lease's holder.
    private static readonly string Holder =
        $"{(Environment.MachineName.Length > 60 ? Environment.MachineName[..60] : Environment.MachineName)}/{Guid.NewGuid():N}";

    private readonly IJokeProfileRepository profiles;
    private readonly ISchedulerLockRepository locks;
    private readonly IJevClient jev;
    private readonly ILlmClientFactory llmClientFactory;
    private readonly IOptions<SimilarityOptions> options;
    private readonly SimilarityMetrics metrics;
    private readonly ILogger<JokeProfiler> logger;

    public JokeProfiler(
        IJokeProfileRepository profiles,
        ISchedulerLockRepository locks,
        IJevClient jev,
        ILlmClientFactory llmClientFactory,
        IOptions<SimilarityOptions> options,
        SimilarityMetrics metrics,
        ILogger<JokeProfiler> logger)
    {
        this.profiles = profiles;
        this.locks = locks;
        this.jev = jev;
        this.llmClientFactory = llmClientFactory;
        this.options = options;
        this.metrics = metrics;
        this.logger = logger;
    }

    /// <summary>What both methods see: the joke and, if it has one, why it's funny (as in the experiment).</summary>
    public static string Input(Joke joke)
        => string.IsNullOrWhiteSpace(joke.Explanation) ? joke.Text : $"{joke.Text}\n\n(Why it's funny: {joke.Explanation})";

    public static string JevVersion(SimilarityOptions options) => JevQuestions.Version(options.Jev.Model);

    public static string EmbeddingVersion(SimilarityOptions options) => $"{options.Embeddings.Deployment}/{options.Embeddings.Dimensions}";

    public async Task<int> ProfileAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Jev.Enabled && !settings.Embeddings.Enabled)
            return 0;
        var now = DateTime.UtcNow;
        if (!await locks.TryAcquireAsync(LeaseKey, Holder, now, now + LeaseLength, cancellationToken))
        {
            logger.LogInformation("Another replica is profiling jokes; this tick leaves it to that one.");
            return 0;
        }

        var saved = 0;
        if (settings.Jev.Enabled)
            saved += await ProfileWithJevAsync(settings, cancellationToken);
        if (settings.Embeddings.Enabled)
            saved += await ProfileWithEmbeddingsAsync(settings, cancellationToken);
        return saved;
    }

    private async Task<int> ProfileWithJevAsync(SimilarityOptions settings, CancellationToken cancellationToken)
    {
        var version = JevVersion(settings);
        var jokes = await profiles.GetJokesWithoutAsync(JokeProfile.JevKind, version, settings.BatchSize, cancellationToken);
        var saved = 0;
        // One at a time: a batch is a few dozen jokes at most, and Jev answers in well under a second.
        foreach (var joke in jokes)
        {
            float[] vector;
            try
            {
                vector = await jev.ProfileAsync(Input(joke), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                metrics.RecordProfile(JokeProfile.JevKind, "failed");
                logger.LogWarning(ex, "Jev couldn't profile joke {JokeId}; the rest of this batch waits for the next tick.", joke.Id);
                break;
            }
            await SaveAsync(joke.Id, JokeProfile.JevKind, version, vector, cancellationToken);
            saved++;
        }
        if (saved > 0)
            logger.LogInformation("Jev profiled {Count} joke(s).", saved);
        return saved;
    }

    private async Task<int> ProfileWithEmbeddingsAsync(SimilarityOptions settings, CancellationToken cancellationToken)
    {
        var version = EmbeddingVersion(settings);
        var jokes = await profiles.GetJokesWithoutAsync(JokeProfile.EmbeddingKind, version, settings.BatchSize, cancellationToken);
        if (jokes.Count == 0)
            return 0;

        using var generator = llmClientFactory.CreateEmbeddingGenerator(settings.Embeddings.Provider, settings.Embeddings.Deployment);
        var saved = 0;
        foreach (var batch in jokes.Chunk(EmbeddingBatch))
        {
            GeneratedEmbeddings<Embedding<float>> embeddings;
            try
            {
                embeddings = await generator.GenerateAsync(batch.Select(Input),
                    new EmbeddingGenerationOptions { Dimensions = settings.Embeddings.Dimensions }, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                metrics.RecordProfile(JokeProfile.EmbeddingKind, "failed");
                logger.LogWarning(ex, "The embedding model couldn't profile {Count} joke(s); they wait for the next tick.", batch.Length);
                break;
            }
            for (var i = 0; i < batch.Length; i++)
            {
                await SaveAsync(batch[i].Id, JokeProfile.EmbeddingKind, version, embeddings[i].Vector.ToArray(), cancellationToken);
                saved++;
            }
        }
        if (saved > 0)
            logger.LogInformation("Embedded {Count} joke(s).", saved);
        return saved;
    }

    private async Task SaveAsync(int jokeId, string kind, string version, float[] vector, CancellationToken cancellationToken)
    {
        await profiles.SaveAsync(new JokeProfile
        {
            JokeId = jokeId,
            Kind = kind,
            Version = version,
            Vector = ToBytes(vector),
            CreatedAt = DateTime.UtcNow,
        }, cancellationToken);
        metrics.RecordProfile(kind, "saved");
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return vector;
    }
}
