using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;

namespace LazyDad.Tests;

public class JokeGenerationServiceTests
{
    private readonly Mock<IJokeRepository> jokeRepositoryMock = new();
    private readonly Mock<ILlmClientFactory> llmClientFactoryMock = new();
    private readonly Mock<IChatClient> chatClientMock = new();

    private readonly LlmModelOptions defaultModel = new()
    {
        Provider = "AzureOpenAI",
        Model = "gpt-5.4-mini"
    };

    private readonly LanguageOptions russianLanguage = new()
    {
        Language = "Russian",
        Enabled = true,
        IntervalHours = 24,
        PromptHint = "Write the joke in Russian.",
        LlmModels = [new() { Provider = "AzureOpenAI", Model = "gpt-5.4-mini" }]
    };

    private JokeGenerationService CreateService(int uniquenessSampleSize = 20)
    {
        var options = Options.Create(new JokeGenerationOptions
        {
            UniquenessSampleSize = uniquenessSampleSize,
            Languages = [russianLanguage]
        });

        return new JokeGenerationService(jokeRepositoryMock.Object, llmClientFactoryMock.Object, options);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsJokeTextFromLlm()
    {
        const string expectedJoke = "Why did the scarecrow win an award? Because he was outstanding in his field!";

        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Russian", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        llmClientFactoryMock
            .Setup(f => f.CreateClient("AzureOpenAI", "gpt-5.4-mini"))
            .Returns(chatClientMock.Object);

        chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, expectedJoke)]));

        var service = CreateService();
        var result = await service.GenerateAsync(russianLanguage, defaultModel, CancellationToken.None);

        Assert.Equal(expectedJoke, result);
    }

    [Fact]
    public async Task GenerateAsync_PassesRecentJokesToPrompt()
    {
        var recentJokes = new List<Joke>
        {
            new() { Language = "Russian", Text = "Joke one", GeneratedAt = DateTime.UtcNow },
            new() { Language = "Russian", Text = "Joke two", GeneratedAt = DateTime.UtcNow }
        };

        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Russian", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recentJokes);

        llmClientFactoryMock
            .Setup(f => f.CreateClient("AzureOpenAI", "gpt-5.4-mini"))
            .Returns(chatClientMock.Object);

        IEnumerable<ChatMessage>? capturedMessages = null;
        chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, "A new joke")]));

        var service = CreateService();
        await service.GenerateAsync(russianLanguage, defaultModel, CancellationToken.None);

        Assert.NotNull(capturedMessages);
        var systemPrompt = capturedMessages.First().Text;
        Assert.Contains("Joke one", systemPrompt);
        Assert.Contains("Joke two", systemPrompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithNoRecentJokes_DoesNotIncludeExclusionList()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(russianLanguage, []);

        Assert.DoesNotContain("already-used", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithRecentJokes_IncludesAllJokesInExclusionList()
    {
        var jokes = new List<string> { "First joke", "Second joke" };

        var prompt = JokeGenerationService.BuildSystemPrompt(russianLanguage, jokes);

        Assert.Contains("First joke", prompt);
        Assert.Contains("Second joke", prompt);
        Assert.Contains("already-used", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesPromptHint()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(russianLanguage, []);

        Assert.Contains(russianLanguage.PromptHint, prompt);
    }
}
