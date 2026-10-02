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

    public async Task<string> GenerateAsync(LanguageOptions language, LlmModelOptions model, CancellationToken cancellationToken)
    {
        var recentJokes = await jokeRepository.GetRecentByLanguageAsync(
            language.Language,
            options.Value.UniquenessSampleSize,
            cancellationToken);

        var prompt = BuildSystemPrompt(language, recentJokes.Select(j => j.Text).ToList());

        using var chatClient = llmClientFactory.CreateClient(model.Provider, model.Model);

        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, prompt),
                new ChatMessage(ChatRole.User, "Generate the joke now.")
            ],
            cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? string.Empty;
    }

    internal static string BuildSystemPrompt(LanguageOptions language, IReadOnlyList<string> recentJokes)
    {
        var sb = new StringBuilder();
        // "Dad joke" names a style, not a subject. Without saying so, models writing in a language with no such term
        // (Ukrainian) took it literally: every joke starred a father ("Тато взяв … бо чув, що …"), and the recent jokes
        // below, all in that mould, taught the next ones to copy it.
        sb.AppendLine("You write dad jokes. A dad joke is a style of humour, not a subject: a short, corny, groan-worthy joke");
        sb.AppendLine("built on a pun or wordplay, the kind a father tells at the dinner table. It doesn't need a father in it,");
        sb.AppendLine("and most good ones don't have one. Generate exactly one original dad joke.");

        if (!string.IsNullOrWhiteSpace(language.PromptHint))
            sb.AppendLine(language.PromptHint);

        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- The joke must be a classic dad joke, built on a pun or wordplay that works in the joke's own language (not a");
        sb.AppendLine("  translated English pun). It can be a one-liner or a short dialogue.");
        sb.AppendLine("- Don't make a father (\"dad\", in any language) a character unless the pun itself needs one.");
        sb.AppendLine("- Vary the form: a question and its answer, a one-liner, a mini-dialogue of two to four lines (each line on");
        sb.AppendLine("  its own line, starting with a dash), or a mock definition.");
        sb.AppendLine("  Pick any everyday subject: food, work, school, animals, weather, technology, sport, places.");
        sb.AppendLine("- The joke must be family-friendly.");
        sb.AppendLine("- Do NOT mention \"Russia\" or \"Russian\" anywhere in the joke.");

        if (recentJokes.Count > 0)
        {
            sb.AppendLine("- Do NOT repeat any of the following already-used jokes, and don't reuse their openings, characters or");
            sb.AppendLine("  structure (e.g. the same first words, or the same \"someone took X because they heard Y\" template):");
            // One line each: a dialogue's lines are joined with " / ", so they don't read as rules or other jokes.
            foreach (var joke in recentJokes)
                sb.AppendLine($"  * {string.Join(" / ", joke.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}");
        }

        sb.AppendLine("- Respond with ONLY the joke text. No explanation, no numbering, no quotes.");

        return sb.ToString();
    }
}
