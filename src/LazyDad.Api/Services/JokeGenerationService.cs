using System.Text;
using LazyDad.Api.Configuration;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

public class JokeGenerationService
{
    private readonly IJokeRepository jokeRepository;
    private readonly ILlmClientFactory llmClientFactory;
    private readonly IOptions<JokeGenerationOptions> options;

    public JokeGenerationService(
        IJokeRepository jokeRepository,
        ILlmClientFactory llmClientFactory,
        IOptions<JokeGenerationOptions> options)
    {
        this.jokeRepository = jokeRepository;
        this.llmClientFactory = llmClientFactory;
        this.options = options;
    }

    public async Task<string> GenerateAsync(LanguageOptions language, CancellationToken cancellationToken)
    {
        var recentJokes = await jokeRepository.GetRecentByLanguageAsync(
            language.Language,
            options.Value.UniquenessSampleSize,
            cancellationToken);

        var prompt = BuildSystemPrompt(language, recentJokes.Select(j => j.Text).ToList());

        using var chatClient = llmClientFactory.CreateClient(language.LlmProvider, language.LlmModel);

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.System, prompt)],
            cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? string.Empty;
    }

    internal static string BuildSystemPrompt(LanguageOptions language, IReadOnlyList<string> recentJokes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a dad joke generator. Generate exactly one original dad joke.");

        if (!string.IsNullOrWhiteSpace(language.PromptHint))
            sb.AppendLine(language.PromptHint);

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- The joke must be a classic dad joke: a pun, wordplay, or groan-worthy one-liner.");
        sb.AppendLine("- The joke must be family-friendly.");

        if (recentJokes.Count > 0)
        {
            sb.AppendLine("- Do NOT repeat any of the following already-used jokes:");
            foreach (var joke in recentJokes)
                sb.AppendLine($"  * {joke}");
        }

        sb.AppendLine("- Respond with ONLY the joke text. No explanation, no numbering, no quotes.");

        return sb.ToString();
    }
}
