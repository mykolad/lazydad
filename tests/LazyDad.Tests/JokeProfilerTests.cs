using System.Diagnostics.Metrics;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public sealed class JokeProfilerTests : IDisposable
{
    private readonly Mock<IJokeProfileRepository> profilesMock = new();
    private readonly Mock<ISchedulerLockRepository> locksMock = new();
    private readonly Mock<IJevClient> jevMock = new();
    private readonly Mock<ILlmClientFactory> llmClientFactoryMock = new();
    private readonly Mock<IEmbeddingGenerator<string, Embedding<float>>> embeddingsMock = new();
    private readonly ServiceProvider metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly List<JokeProfile> saved = [];
    private readonly SimilarityOptions options = new() { BatchSize = 10, Jev = { ApiKey = "key" } };
    private readonly Clock clock = new();

    public JokeProfilerTests()
    {
        profilesMock
            .Setup(r => r.SaveAsync(It.IsAny<JokeProfile>(), It.IsAny<CancellationToken>()))
            .Callback<JokeProfile, CancellationToken>((profile, _) => saved.Add(profile))
            .Returns(Task.CompletedTask);
        profilesMock
            .Setup(r => r.GetJokesWithoutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        // This replica gets the profiling lease unless a test says otherwise.
        locksMock
            .Setup(l => l.TryAcquireAsync(JokeProfiler.LeaseKey, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        llmClientFactoryMock
            .Setup(f => f.CreateEmbeddingGenerator("AzureOpenAI", "text-embedding-3-small"))
            .Returns(embeddingsMock.Object);
    }

    public void Dispose() => metricsProvider.Dispose();

    private IMeterFactory Meters => metricsProvider.GetRequiredService<IMeterFactory>();

    private JokeProfiler CreateProfiler()
        => new(profilesMock.Object, locksMock.Object, jevMock.Object, llmClientFactoryMock.Object, Options.Create(options),
            new SimilarityMetrics(Meters), NullLogger<JokeProfiler>.Instance, clock);

    private static Joke MakeJoke(int id, string? explanation) => new() { Id = id, Language = "Ukrainian", Text = $"Joke {id}", Explanation = explanation };

    private void Lacking(string kind, params Joke[] jokes)
        => profilesMock
            .Setup(r => r.GetJokesWithoutAsync(kind, It.IsAny<string>(), 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. jokes]);

    [Fact]
    public async Task ProfileAsync_GivesEachJokeWithoutAJevProfileOne_WithTheJokeAndItsExplanation()
    {
        Lacking(JokeProfile.JevKind, MakeJoke(5, "A pun."), MakeJoke(4, null));
        jevMock.Setup(j => j.ProfileAsync("Joke 5\n\n(Why it's funny: A pun.)", It.IsAny<CancellationToken>())).ReturnsAsync([0.5f, 0.25f]);
        jevMock.Setup(j => j.ProfileAsync("Joke 4", It.IsAny<CancellationToken>())).ReturnsAsync([1f, 0f]);

        var count = await CreateProfiler().ProfileAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.All(saved, p => Assert.Equal(JokeProfile.JevKind, p.Kind));
        Assert.All(saved, p => Assert.Equal("jev-1.13.0/q1", p.Version));
        Assert.Equal([5, 4], saved.Select(p => p.JokeId));
        Assert.Equal([0.5f, 0.25f], JokeProfiler.FromBytes(saved[0].Vector));
    }

    [Fact]
    public async Task ProfileAsync_StopsAskingJevForThisBatch_AfterAFailure_AndCountsIt()
    {
        Lacking(JokeProfile.JevKind, MakeJoke(3, null), MakeJoke(2, null), MakeJoke(1, null));
        jevMock.Setup(j => j.ProfileAsync("Joke 3", It.IsAny<CancellationToken>())).ReturnsAsync([1f]);
        jevMock.Setup(j => j.ProfileAsync("Joke 2", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("402"));
        using var profiles = new MetricCollector<long>(Meters, LazyDadTelemetry.Name, "lazydad.joke.profiles");

        var count = await CreateProfiler().ProfileAsync(CancellationToken.None);

        Assert.Equal(1, count);
        jevMock.Verify(j => j.ProfileAsync("Joke 1", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(["jev/failed", "jev/saved"],
            profiles.GetMeasurementSnapshot().Where(m => m.Value != 0).Select(m => $"{m.Tags["kind"]}/{m.Tags["outcome"]}").Order());
    }

    [Fact]
    public async Task ProfileAsync_WithoutAJevKey_OnlyEmbeds()
    {
        options.Jev.ApiKey = "";
        Lacking(JokeProfile.EmbeddingKind, MakeJoke(1, null), MakeJoke(2, "Why."));
        embeddingsMock
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
                new GeneratedEmbeddings<Embedding<float>>(values.Select((_, i) => new Embedding<float>(new float[] { i, 1 }))));

        var count = await CreateProfiler().ProfileAsync(CancellationToken.None);

        Assert.Equal(2, count);
        jevMock.VerifyNoOtherCalls();
        profilesMock.Verify(r => r.GetJokesWithoutAsync(JokeProfile.JevKind, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // One request for the batch, at the configured size, with the same input as Jev's.
        embeddingsMock.Verify(g => g.GenerateAsync(
            It.Is<IEnumerable<string>>(v => v.SequenceEqual(new[] { "Joke 1", "Joke 2\n\n(Why it's funny: Why.)" })),
            It.Is<EmbeddingGenerationOptions?>(o => o!.Dimensions == 512), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["embedding:1:text-embedding-3-small/512", "embedding:2:text-embedding-3-small/512"],
            saved.Select(p => $"{p.Kind}:{p.JokeId}:{p.Version}"));
        Assert.Equal([1f, 1f], JokeProfiler.FromBytes(saved[1].Vector));
    }

    [Fact]
    public async Task ProfileAsync_WhenTheEmbeddingModelFails_SavesNothing_AndDoesNotThrow()
    {
        Lacking(JokeProfile.EmbeddingKind, MakeJoke(1, null));
        embeddingsMock
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("deployment not found"));

        Assert.Equal(0, await CreateProfiler().ProfileAsync(CancellationToken.None));
        Assert.Empty(saved);
    }

    [Fact]
    public void VectorBytes_RoundTrip()
        => Assert.Equal([1.5f, -2f, 0f], JokeProfiler.FromBytes(JokeProfiler.ToBytes([1.5f, -2f, 0f])));

    [Fact]
    public async Task ProfileAsync_WhileAnotherReplicaHoldsTheLease_DoesNothing()
    {
        Lacking(JokeProfile.JevKind, MakeJoke(1, null));
        locksMock
            .Setup(l => l.TryAcquireAsync(JokeProfiler.LeaseKey, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.Equal(0, await CreateProfiler().ProfileAsync(CancellationToken.None));
        jevMock.VerifyNoOtherCalls();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task ProfileAsync_TakesTheLeaseForHalfAnHour()
    {
        DateTime? from = null, until = null;
        locksMock
            .Setup(l => l.TryAcquireAsync(JokeProfiler.LeaseKey, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateTime, DateTime, CancellationToken>((_, _, now, expires, _) => (from, until) = (now, expires))
            .ReturnsAsync(true);

        await CreateProfiler().ProfileAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), until - from);
    }

    [Fact]
    public async Task ProfileAsync_StopsBeforeTheLeaseEnds_AndLeavesTheRestForTheNextTick()
    {
        Lacking(JokeProfile.JevKind, [.. Enumerable.Range(1, 5).Select(i => MakeJoke(i, null))]);
        // Every answer takes 10 minutes: calls start at 0, 10 and 20; at 30 the lease (30 minutes) would be over.
        jevMock
            .Setup(j => j.ProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => clock.Now += TimeSpan.FromMinutes(10))
            .ReturnsAsync([1f]);

        var count = await CreateProfiler().ProfileAsync(CancellationToken.None);

        Assert.Equal(3, count);
        jevMock.Verify(j => j.ProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ProfileAsync_ReleasesTheLease_EvenWhenSavingFails()
    {
        Lacking(JokeProfile.JevKind, MakeJoke(1, null));
        jevMock.Setup(j => j.ProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([1f]);
        profilesMock
            .Setup(r => r.SaveAsync(It.IsAny<JokeProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database is busy."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProfiler().ProfileAsync(CancellationToken.None));

        locksMock.Verify(l => l.ReleaseAsync(JokeProfiler.LeaseKey, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
