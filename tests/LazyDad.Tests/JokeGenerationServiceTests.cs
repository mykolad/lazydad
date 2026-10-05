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

    private readonly LanguageOptions ukrainianLanguage = new()
    {
        Language = "Ukrainian",
        Enabled = true,
        IntervalHours = 24,
        PromptHint = "Write the joke in Ukrainian.",
        LlmModels = [new() { Provider = "AzureOpenAI", Model = "gpt-5.4-mini" }]
    };

    private JokeGenerationService CreateService(int uniquenessSampleSize = 20)
    {
        var options = Options.Create(new JokeGenerationOptions
        {
            UniquenessSampleSize = uniquenessSampleSize,
            Languages = [ukrainianLanguage]
        });

        return new JokeGenerationService(jokeRepositoryMock.Object, llmClientFactoryMock.Object, options);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsJokeTextFromLlm()
    {
        const string expectedJoke = "Why did the scarecrow win an award? Because he was outstanding in his field!";

        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Ukrainian", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        llmClientFactoryMock
            .Setup(f => f.CreateClient("AzureOpenAI", "gpt-5.4-mini"))
            .Returns(chatClientMock.Object);

        chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant, expectedJoke)]));

        var service = CreateService();
        var result = await service.GenerateAsync(ukrainianLanguage, defaultModel, [], CancellationToken.None);

        Assert.Equal(new JokeDraft(expectedJoke, null), result);
    }


    [Fact]
    public async Task GenerateAsync_SplitsTheJokeFromItsExplanation()
    {
        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Ukrainian", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        llmClientFactoryMock
            .Setup(f => f.CreateClient("AzureOpenAI", "gpt-5.4-mini"))
            .Returns(chatClientMock.Object);
        chatClientMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse([new ChatMessage(ChatRole.Assistant,
                "— Чому годинник пішов?\r\n— Бо мав багато часу!\r\n---\r\n\"Пішов\" means both \"left\" and \"started running\".")]));

        var result = await CreateService().GenerateAsync(ukrainianLanguage, defaultModel, [], CancellationToken.None);

        Assert.Equal("— Чому годинник пішов?\n— Бо мав багато часу!", result.Text);
        Assert.Equal("\"Пішов\" means both \"left\" and \"started running\".", result.Explanation);
    }

    [Theory]
    [InlineData("A joke\n---\n", "A joke", null)]                                // nothing after the separator
    [InlineData("A joke\n  ---  \nIt's a pun\non a word.", "A joke", "It's a pun on a word.")]
    [InlineData("A joke -- with dashes --- inside\n---\nWhy", "A joke -- with dashes --- inside", "Why")]
    [InlineData("  ", "", null)]                                                 // empty: the scheduler skips it
    public void Parse_SplitsAtTheFirstSeparatorLine(string answer, string text, string? explanation)
    {
        Assert.Equal(new JokeDraft(text, explanation), JokeGenerationService.Parse(answer));
    }

    [Fact]
    public void Parse_CutsALongExplanationToTheColumnsSize()
    {
        var draft = JokeGenerationService.Parse($"A joke\n---\n{new string('x', 600)}");

        Assert.Equal(500, draft.Explanation!.Length);
    }

    [Fact]
    public void BuildSystemPrompt_AsksForTheExplanationAfterTheSeparator()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, [], []);

        Assert.Contains($"a line with only {JokeGenerationService.Separator}", prompt);
    }
    [Fact]
    public async Task GenerateAsync_PassesRecentJokesToPrompt()
    {
        var recentJokes = new List<Joke>
        {
            new() { Language = "Ukrainian", Text = "Joke one", GeneratedAt = DateTime.UtcNow },
            new() { Language = "Ukrainian", Text = "Joke two", GeneratedAt = DateTime.UtcNow }
        };

        jokeRepositoryMock
            .Setup(r => r.GetRecentByLanguageAsync("Ukrainian", 20, It.IsAny<CancellationToken>()))
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
        await service.GenerateAsync(ukrainianLanguage, defaultModel, [], CancellationToken.None);

        Assert.NotNull(capturedMessages);
        var systemPrompt = capturedMessages.First().Text;
        Assert.Contains("Joke one", systemPrompt);
        Assert.Contains("Joke two", systemPrompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithNoRecentJokes_DoesNotIncludeExclusionList()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, [], []);

        Assert.DoesNotContain("already-used", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithRecentJokes_IncludesAllJokesInExclusionList()
    {
        var jokes = new List<string> { "First joke", "Second joke" };

        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, jokes, []);

        Assert.Contains("First joke", prompt);
        Assert.Contains("Second joke", prompt);
        Assert.Contains("already-used", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_TreatsADadJokeAsAStyle_AndAsksForVariety()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, ["Тато взяв гарбуз на збори, бо чув, що там ділитимуть гарбузи."], []);

        Assert.Contains("a style of humour, not a subject", prompt);
        Assert.Contains("Don't make a father", prompt);
        Assert.Contains("Vary the form", prompt);
        // The recent jokes are listed to avoid their templates too, not only their exact text.
        Assert.Contains("don't reuse their openings", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ListsARecentDialogueOnOneLine()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, ["— Чому ти спізнився?\r\n— Годинник відстає!"], []);

        Assert.Contains("  * — Чому ти спізнився? / — Годинник відстає!", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesPromptHint()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, [], []);

        Assert.Contains(ukrainianLanguage.PromptHint, prompt);
    }

    [Fact]
    public void BuildSystemPrompt_NamesTheJokesTheModelRepeated_AndAsksForADifferentOne()
    {
        var prompt = JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, [], ["Чому гречка стала бухгалтеркою?\nБо рахувала крупні витрати!"]);

        Assert.Contains("repeated a joke the site already has", prompt);
        Assert.Contains("  * Чому гречка стала бухгалтеркою? / Бо рахувала крупні витрати!", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_WithoutRepeats_SaysNothingAboutThem()
        => Assert.DoesNotContain("repeated a joke", JokeGenerationService.BuildSystemPrompt(ukrainianLanguage, [], []));
}
