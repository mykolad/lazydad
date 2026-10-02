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

    /// <summary>The joke, and why it's funny (null if the model left it out). An empty text means the model returned nothing.</summary>
    public async Task<JokeDraft> GenerateAsync(LanguageOptions language, LlmModelOptions model, CancellationToken cancellationToken)
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

        return Parse(response.Text);
    }

    // Between the joke and its explanation in the model's answer: a line of its own, which no joke contains.
    internal const string Separator = "---";
    // The column's size (LazyDadDbContext); the prompt asks for 200 characters, a longer one is cut.
    private const int MaxExplanationLength = 500;

    /// <summary>
    /// Splits the model's answer at the first line that is only <see cref="Separator"/>. Without one, the whole answer
    /// is the joke and there's no explanation: a model that ignores the format still gives a joke.
    /// </summary>
    internal static JokeDraft Parse(string? answer)
    {
        var lines = (answer ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        var separator = Array.FindIndex(lines, line => line.Trim() == Separator);
        if (separator < 0)
            return new JokeDraft((answer ?? string.Empty).Trim(), null);

        var joke = string.Join('\n', lines[..separator]).Trim();
        var explanation = string.Join(' ', lines[(separator + 1)..].Select(l => l.Trim()).Where(l => l.Length > 0));
        return new JokeDraft(joke, explanation.Length == 0 ? null
            : explanation.Length <= MaxExplanationLength ? explanation : explanation[..MaxExplanationLength]);
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

        sb.AppendLine($"- Respond with the joke, then a line with only {Separator}, then one sentence in English (at most 200");
        sb.AppendLine("  characters) explaining the wordplay to someone who didn't get it. Nothing else: no numbering, no quotes, no");
        sb.AppendLine("  labels such as \"Joke:\" or \"Explanation:\".");

        return sb.ToString();
    }
}

/// <summary>A generated joke before it's saved: its text and, if the model gave one, why it's funny.</summary>
public sealed record JokeDraft(string Text, string? Explanation);
