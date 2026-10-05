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
    // Longer than a batch usually takes (a few hundred answers of well under a second). Released when the batch is done,
    // so a restart or deploy right after a tick can profile; it only expires on its own if this process dies mid-batch.
    public static readonly TimeSpan LeaseLength = TimeSpan.FromMinutes(30);
    // No new Jev call or embedding batch this close to the lease's end: the slowest single call (Jev's 30-second
    // timeout, the Azure OpenAI client's 100 seconds) still ends while this replica holds it, so a slow batch stops
    // early and leaves the rest to the next tick instead of overlapping another replica's.
    public static readonly TimeSpan StopBeforeLeaseEnds = TimeSpan.FromMinutes(5);
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
    private readonly TimeProvider time;

    public JokeProfiler(
        IJokeProfileRepository profiles,
        ISchedulerLockRepository locks,
        IJevClient jev,
        ILlmClientFactory llmClientFactory,
        IOptions<SimilarityOptions> options,
        SimilarityMetrics metrics,
        ILogger<JokeProfiler> logger)
        : this(profiles, locks, jev, llmClientFactory, options, metrics, logger, TimeProvider.System)
    {
    }

    /// <summary>For tests: a fake clock.</summary>
    internal JokeProfiler(
        IJokeProfileRepository profiles,
        ISchedulerLockRepository locks,
        IJevClient jev,
        ILlmClientFactory llmClientFactory,
        IOptions<SimilarityOptions> options,
        SimilarityMetrics metrics,
        ILogger<JokeProfiler> logger,
        TimeProvider time)
    {
        this.profiles = profiles;
        this.locks = locks;
        this.jev = jev;
        this.llmClientFactory = llmClientFactory;
        this.options = options;
        this.metrics = metrics;
        this.logger = logger;
        this.time = time;
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
        var now = time.GetUtcNow().UtcDateTime;
        if (!await locks.TryAcquireAsync(LeaseKey, Holder, now, now + LeaseLength, cancellationToken))
        {
            logger.LogInformation("Another replica is profiling jokes; this tick leaves it to that one.");
            return 0;
        }

        var stopAt = now + LeaseLength - StopBeforeLeaseEnds;
        var saved = 0;
        try
        {
            if (settings.Jev.Enabled)
                saved += await ProfileWithJevAsync(settings, stopAt, cancellationToken);
            if (settings.Embeddings.Enabled)
                saved += await ProfileWithEmbeddingsAsync(settings, stopAt, cancellationToken);
        }
        finally
        {
            // Not with the tick's token: a shutdown mid-batch should still free the lease for the next replica.
            await locks.ReleaseAsync(LeaseKey, Holder, time.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        return saved;
    }

    private bool PastDeadline(DateTime stopAt, string method)
    {
        if (time.GetUtcNow().UtcDateTime < stopAt)
            return false;
        logger.LogWarning("Profiling with {Method} ran close to the end of its lease; the rest waits for the next tick.", method);
        return true;
    }

    private async Task<int> ProfileWithJevAsync(SimilarityOptions settings, DateTime stopAt, CancellationToken cancellationToken)
    {
        var version = JevVersion(settings);
        var jokes = await profiles.GetJokesWithoutAsync(JokeProfile.JevKind, version, settings.BatchSize, cancellationToken);
        var saved = 0;
        // One at a time: a batch is a few dozen jokes at most, and Jev answers in well under a second.
        foreach (var joke in jokes)
        {
            if (PastDeadline(stopAt, JokeProfile.JevKind))
                break;
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

    private async Task<int> ProfileWithEmbeddingsAsync(SimilarityOptions settings, DateTime stopAt, CancellationToken cancellationToken)
    {
        var version = EmbeddingVersion(settings);
        var jokes = await profiles.GetJokesWithoutAsync(JokeProfile.EmbeddingKind, version, settings.BatchSize, cancellationToken);
        if (jokes.Count == 0)
            return 0;

        using var generator = llmClientFactory.CreateEmbeddingGenerator(settings.Embeddings.Provider, settings.Embeddings.Deployment);
        var saved = 0;
        foreach (var batch in jokes.Chunk(EmbeddingBatch))
        {
            if (PastDeadline(stopAt, JokeProfile.EmbeddingKind))
                break;
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
            // The whole batch in one write: a hundred row-by-row writes after a slow model call could outlast the lease.
            var batchProfiles = batch.Select((joke, i) => Profile(joke.Id, JokeProfile.EmbeddingKind, version, embeddings[i].Vector.ToArray())).ToList();
            try
            {
                await profiles.SaveAllAsync(batchProfiles, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                metrics.RecordProfile(JokeProfile.EmbeddingKind, "failed");
                throw;
            }
            foreach (var _ in batchProfiles)
                metrics.RecordProfile(JokeProfile.EmbeddingKind, "saved");
            saved += batchProfiles.Count;
        }
        if (saved > 0)
            logger.LogInformation("Embedded {Count} joke(s).", saved);
        return saved;
    }

    private async Task SaveAsync(int jokeId, string kind, string version, float[] vector, CancellationToken cancellationToken)
    {
        try
        {
            await profiles.SaveAsync(Profile(jokeId, kind, version, vector), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Counted, so LazyDadProfileFailed fires for a profile that couldn't be stored too; the scheduler logs the error.
            metrics.RecordProfile(kind, "failed");
            throw;
        }
        metrics.RecordProfile(kind, "saved");
    }

    private JokeProfile Profile(int jokeId, string kind, string version, float[] vector) => new()
    {
        JokeId = jokeId,
        Kind = kind,
        Version = version,
        Vector = ToBytes(vector),
        CreatedAt = time.GetUtcNow().UtcDateTime,
    };

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
