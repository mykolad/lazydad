using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class TopJokeServiceTests
{
    private const string Language = "Ukrainian";

    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly Mock<ITopJokeRepository> topJokeRepositoryMock = new();
    private readonly Mock<ILlmClientFactory> llmClientFactoryMock = new();
    private readonly Mock<IChatClient> chatClientMock = new();

    private readonly TopJokesOptions topJokesOptions = new()
    {
        Enabled = true,
        Size = 3,
        SeedSampleSize = 100,
        Judge = new() { Provider = "AzureOpenAI", Model = "gpt-6-sol" },
        JudgeReasoningEffort = ReasoningEffort.High
    };

    public TopJokeServiceTests()
    {
        llmClientFactoryMock
            .Setup(f => f.CreateClient("AzureOpenAI", "gpt-6-sol"))
            .Returns(chatClientMock.Object);
    }

    private TopJokeService CreateService()
        => new(
            jokeRepositoryMock.Object,
            topJokeRepositoryMock.Object,
            llmClientFactoryMock.Object,
            Options.Create(topJokesOptions),
            NullLogger<TopJokeService>.Instance);

    private static Joke MakeJoke(int id)
        => new() { Id = id, Language = Language, Model = "gpt-5.3-chat", Text = $"Joke {id}", GeneratedAt = DateTime.UtcNow };

    private static TopJoke MakeTop(int rank, Joke joke)
        => new() { Language = Language, Rank = rank, JokeId = joke.Id, Joke = joke, Reason = "old", JudgeModel = "gpt-6-sol" };

    private void SetupJudgeReply(string json)
        => chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, json)]));

    private void SetupCurrentTop(List<TopJoke> current)
        => topJokeRepositoryMock
            .Setup(r => r.GetByLanguageAsync(Language, It.IsAny<CancellationToken>()))
            .ReturnsAsync(current);

    [Fact]
    public async Task UpdateAsync_WhenLeaderboardEmpty_SeedsFromRecentJokes()
    {
        SetupCurrentTop([]);
        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync(Language, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(MakeJoke).ToList());
        SetupJudgeReply("""{"picks":[{"jokeId":4,"reason":"a"},{"jokeId":2,"reason":"b"},{"jokeId":5,"reason":"c"}]}""");

        IReadOnlyList<TopJoke>? saved = null;
        topJokeRepositoryMock
            .Setup(r => r.ReplaceAsync(Language, It.IsAny<IReadOnlyList<TopJoke>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<TopJoke>, CancellationToken>((_, entries, _) => saved = entries)
            .Returns(Task.CompletedTask);

        var changed = await CreateService().UpdateAsync(Language, [], CancellationToken.None);

        Assert.True(changed);
        Assert.NotNull(saved);
        Assert.Equal([4, 2, 5], saved.Select(e => e.JokeId));
        Assert.Equal([1, 2, 3], saved.Select(e => e.Rank));
        Assert.All(saved, e => Assert.Equal("gpt-6-sol", e.JudgeModel));
    }

    [Fact]
    public async Task UpdateAsync_WhenLeaderboardFullAndNoNewJokes_DoesNotCallJudge()
    {
        SetupCurrentTop([MakeTop(1, MakeJoke(1)), MakeTop(2, MakeJoke(2)), MakeTop(3, MakeJoke(3))]);

        var changed = await CreateService().UpdateAsync(Language, [], CancellationToken.None);

        Assert.False(changed);
        llmClientFactoryMock.Verify(f => f.CreateClient(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_WhenNewJokeWins_ReplacesLeaderboard()
    {
        SetupCurrentTop([MakeTop(1, MakeJoke(1)), MakeTop(2, MakeJoke(2)), MakeTop(3, MakeJoke(3))]);
        SetupJudgeReply("""{"picks":[{"jokeId":1,"reason":"a"},{"jokeId":10,"reason":"b"},{"jokeId":2,"reason":"c"}]}""");

        var changed = await CreateService().UpdateAsync(Language, [MakeJoke(10), MakeJoke(11)], CancellationToken.None);

        Assert.True(changed);
        topJokeRepositoryMock.Verify(r => r.ReplaceAsync(
            Language,
            It.Is<IReadOnlyList<TopJoke>>(e => e.Select(x => x.JokeId).SequenceEqual(new[] { 1, 10, 2 })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WhenJudgeKeepsCurrentOrder_DoesNotWrite()
    {
        SetupCurrentTop([MakeTop(1, MakeJoke(1)), MakeTop(2, MakeJoke(2)), MakeTop(3, MakeJoke(3))]);
        SetupJudgeReply("""{"picks":[{"jokeId":1,"reason":"a"},{"jokeId":2,"reason":"b"},{"jokeId":3,"reason":"c"}]}""");

        var changed = await CreateService().UpdateAsync(Language, [MakeJoke(10)], CancellationToken.None);

        Assert.False(changed);
        topJokeRepositoryMock.Verify(r => r.ReplaceAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TopJoke>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""{"picks":[{"jokeId":1,"reason":"a"},{"jokeId":99,"reason":"b"},{"jokeId":2,"reason":"c"}]}""")] // unknown id
    [InlineData("""{"picks":[{"jokeId":1,"reason":"a"},{"jokeId":1,"reason":"b"},{"jokeId":2,"reason":"c"}]}""")]  // duplicate
    [InlineData("""{"picks":[{"jokeId":1,"reason":"a"},{"jokeId":2,"reason":"b"}]}""")]                             // too few
    [InlineData("not json")]
    public async Task UpdateAsync_WhenVerdictInvalid_DoesNotWrite(string reply)
    {
        SetupCurrentTop([MakeTop(1, MakeJoke(1)), MakeTop(2, MakeJoke(2)), MakeTop(3, MakeJoke(3))]);
        SetupJudgeReply(reply);

        var changed = await CreateService().UpdateAsync(Language, [MakeJoke(10)], CancellationToken.None);

        Assert.False(changed);
        topJokeRepositoryMock.Verify(r => r.ReplaceAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TopJoke>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_WhenDisabled_DoesNothing()
    {
        topJokesOptions.Enabled = false;

        var changed = await CreateService().UpdateAsync(Language, [MakeJoke(10)], CancellationToken.None);

        Assert.False(changed);
        topJokeRepositoryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateAsync_UsesConfiguredReasoningEffort()
    {
        SetupCurrentTop([MakeTop(1, MakeJoke(1)), MakeTop(2, MakeJoke(2)), MakeTop(3, MakeJoke(3))]);

        ChatOptions? captured = null;
        chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "{}")]));

        await CreateService().UpdateAsync(Language, [MakeJoke(10)], CancellationToken.None);

        Assert.Equal(ReasoningEffort.High, captured?.Reasoning?.Effort);
        Assert.IsType<ChatResponseFormatJson>(captured?.ResponseFormat);
    }

    [Fact]
    public void BuildCandidatesPrompt_MarksCurrentLeaders()
    {
        var leader = MakeJoke(1);
        var prompt = TopJokeService.BuildCandidatesPrompt([MakeTop(1, leader)], [leader, MakeJoke(10)]);

        Assert.Contains("[id=1] (CURRENT #1) Joke 1", prompt);
        Assert.Contains("[id=10] Joke 10", prompt);
    }
}
