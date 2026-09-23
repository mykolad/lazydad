using System.Text;
using System.Text.Json;
using LazyDad.Api.Configuration;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

/// <summary>
/// Maintains each language's top-N leaderboard. A (bigger, reasoning) judge LLM compares
/// the current leaders against newly generated jokes and returns the new ranking, which
/// may or may not differ from the current one. When the leaderboard is empty or short,
/// the judge seeds it from the most recent jokes instead.
/// </summary>
public class TopJokeService
{
    private const int MaxReasonLength = 500;

    private readonly IJokeRepository jokeRepository;
    private readonly ITopJokeRepository topJokeRepository;
    private readonly ILlmClientFactory llmClientFactory;
    private readonly IOptions<TopJokesOptions> options;
    private readonly ILogger<TopJokeService> logger;

    public TopJokeService(
        IJokeRepository jokeRepository,
        ITopJokeRepository topJokeRepository,
        ILlmClientFactory llmClientFactory,
        IOptions<TopJokesOptions> options,
        ILogger<TopJokeService> logger)
    {
        this.jokeRepository = jokeRepository;
        this.topJokeRepository = topJokeRepository;
        this.llmClientFactory = llmClientFactory;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Asks the judge whether <paramref name="newJokes"/> change the leaderboard.</summary>
    /// <returns><c>true</c> if the leaderboard was changed.</returns>
    public async Task<bool> UpdateAsync(string language, IReadOnlyList<Joke> newJokes, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return false;

        var current = await topJokeRepository.GetByLanguageAsync(language, cancellationToken);
        var seeding = current.Count < settings.Size;

        if (!seeding && newJokes.Count == 0)
            return false;

        var challengers = seeding
            ? await jokeRepository.GetRecentByLanguageAsync(language, settings.SeedSampleSize, cancellationToken)
            : newJokes;

        var candidates = current.Select(t => t.Joke)
            .Concat(challengers)
            .DistinctBy(j => j.Id)
            .ToList();

        var slots = Math.Min(settings.Size, candidates.Count);
        if (slots == 0)
            return false;

        logger.LogInformation("Judging {Count} candidate(s) for the '{Language}' top {Size} ({Mode}) using {Model}...",
            candidates.Count, language, settings.Size, seeding ? "seeding" : "challenge", settings.Judge.Model);

        var verdict = await AskJudgeAsync(language, current, candidates, slots, cancellationToken);
        var picks = ValidateVerdict(verdict, candidates, slots);

        if (picks is null)
        {
            logger.LogWarning("Judge returned an invalid verdict for '{Language}'; leaderboard unchanged. Raw: {Verdict}",
                language, JsonSerializer.Serialize(verdict, JsonSerializerOptions.Web));
            return false;
        }

        if (picks.Select(p => p.JokeId).SequenceEqual(current.Select(t => t.JokeId)))
        {
            logger.LogInformation("Judge kept the '{Language}' top {Size} unchanged.", language, settings.Size);
            return false;
        }

        var now = DateTime.UtcNow;
        var entries = picks
            .Select((pick, index) => new TopJoke
            {
                Language = language,
                Rank = index + 1,
                JokeId = pick.JokeId,
                Reason = Truncate(pick.Reason.Trim(), MaxReasonLength),
                JudgeModel = settings.Judge.Model,
                SelectedAt = now
            })
            .ToList();

        await topJokeRepository.ReplaceAsync(language, entries, cancellationToken);

        logger.LogInformation("'{Language}' top {Size} updated: {JokeIds}",
            language, settings.Size, string.Join(", ", entries.Select(e => e.JokeId)));

        return true;
    }

    private async Task<JudgeVerdict?> AskJudgeAsync(
        string language,
        IReadOnlyList<TopJoke> current,
        IReadOnlyList<Joke> candidates,
        int slots,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var chatClient = llmClientFactory.CreateClient(settings.Judge.Provider, settings.Judge.Model);

        var chatOptions = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = settings.JudgeReasoningEffort },
            ResponseFormat = ChatResponseFormat.ForJsonSchema<JudgeVerdict>(
                serializerOptions: JsonSerializerOptions.Web,
                schemaName: "JudgeVerdict",
                schemaDescription: "The ranked top jokes, best first.")
        };

        var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, BuildSystemPrompt(language, slots)),
                new ChatMessage(ChatRole.User, BuildCandidatesPrompt(current, candidates))
            ],
            chatOptions,
            cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<JudgeVerdict>(response.Text, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Judge response for '{Language}' was not valid JSON: {Text}", language, response.Text);
            return null;
        }
    }

    internal static string BuildSystemPrompt(string language, int slots)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are a discerning comedy judge ranking dad jokes written in {language}.");
        sb.AppendLine($"From the candidate jokes provided, choose the {slots} best and rank them, best first.");
        sb.AppendLine();
        sb.AppendLine("Judging criteria, in order of importance:");
        sb.AppendLine("- Quality of the pun or wordplay: does the twist actually land for a native speaker?");
        sb.AppendLine("- The groan factor: the best dad jokes make you laugh and roll your eyes at once.");
        sb.AppendLine("- Originality: penalize stale, widely known jokes and weak translations of English puns.");
        sb.AppendLine($"- Natural, grammatical {language}.");
        sb.AppendLine("- Brevity and family-friendliness.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Jokes marked CURRENT already hold a top spot. Only displace or reorder them if a candidate is clearly better.");
        sb.AppendLine($"- Return exactly {slots} distinct joke ids, taken only from the candidate list.");
        sb.AppendLine("- For each pick, give a one-sentence reason in English (max 200 characters).");

        return sb.ToString();
    }

    internal static string BuildCandidatesPrompt(IReadOnlyList<TopJoke> current, IReadOnlyList<Joke> candidates)
    {
        var currentRanks = current.ToDictionary(t => t.JokeId, t => t.Rank);

        var sb = new StringBuilder();
        sb.AppendLine("Candidates:");
        foreach (var joke in candidates)
        {
            var marker = currentRanks.TryGetValue(joke.Id, out var rank) ? $" (CURRENT #{rank})" : string.Empty;
            sb.AppendLine($"[id={joke.Id}]{marker} {joke.Text.ReplaceLineEndings(" ")}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the verdict's picks if they are exactly <paramref name="slots"/> distinct
    /// candidate ids; otherwise <c>null</c>, so a hallucinated verdict never reaches the DB.
    /// </summary>
    internal static IReadOnlyList<JudgePick>? ValidateVerdict(JudgeVerdict? verdict, IReadOnlyList<Joke> candidates, int slots)
    {
        if (verdict?.Picks is null)
            return null;

        var candidateIds = candidates.Select(j => j.Id).ToHashSet();
        var picks = verdict.Picks
            .Where(p => candidateIds.Contains(p.JokeId))
            .DistinctBy(p => p.JokeId)
            .ToList();

        // Every pick must be a real, distinct candidate — any drop means the judge went off-script.
        if (picks.Count != verdict.Picks.Count || picks.Count != slots)
            return null;

        return picks;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength];

    internal sealed record JudgeVerdict(List<JudgePick> Picks);

    internal sealed record JudgePick(int JokeId, string Reason);
}
