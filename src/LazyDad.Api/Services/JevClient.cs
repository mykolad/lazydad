using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LazyDad.Api.Configuration;
using LazyDad.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace LazyDad.Api.Services;

/// <summary>A joke's Jev profile: the probability of every topic, then of every kind of wordplay (in <see cref="JevQuestions"/> order).</summary>
public interface IJevClient
{
    Task<float[]> ProfileAsync(string input, CancellationToken cancellationToken);
}

/// <summary>
/// The questions Jev answers about each joke: what it's about, and what kind of wordplay makes it work. Each answer is a
/// probability for every option, so two jokes are close when their probabilities are. These are the questions the
/// similarity experiment (experiments/similarity) used, which won its blind test against embeddings.
/// </summary>
public static class JevQuestions
{
    /// <summary>Changes whenever a question or an option changes: older profiles then aren't comparable any more.</summary>
    public const string QuestionSet = "q1";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Topics =
    [
        new("food", "food, cooking, drinks, kitchen, vegetables, fruit"),
        new("school", "school, lessons, teachers, studying, exams, grammar"),
        new("work", "work, office, jobs, bosses, meetings, professions"),
        new("animals", "animals, pets, birds, insects, fish"),
        new("transport", "transport, cars, buses, trams, trains, roads, travel"),
        new("technology", "computers, phones, internet, gadgets, electricity"),
        new("weather", "weather, seasons, rain, snow, sun, wind"),
        new("sport", "sport, games, fitness, football, chess"),
        new("home", "home, furniture, household items, chores, repairs, tools"),
        new("nature", "nature, plants, forests, gardens, mushrooms, farming"),
        new("money", "money, shopping, prices, banks, markets"),
        new("music", "music, songs, instruments, singing, dancing"),
        new("health", "health, doctors, medicine, body, sleep"),
        new("places", "towns, cities, regions, landmarks, geography"),
        new("holidays", "holidays, celebrations, traditions, gifts"),
        new("time", "time, clocks, calendars, schedules, being late"),
        new("clothes", "clothes, shoes, fashion, sewing, tailors"),
        new("family", "family members, relatives, children, parents"),
        new("art", "art, books, writing, painting, crafts, theatre, films"),
        new("science", "science, maths, physics, chemistry, space"),
        new("language", "words, letters, language, communication, letters and post"),
        new("other", "none of the above"),
    ];

    public static readonly IReadOnlyList<KeyValuePair<string, string>> Wordplay =
    [
        new("double_meaning", "one word or phrase read with two meanings"),
        new("sound_alike", "words that sound alike or share a root (a homophone, near-homophone or similar-sounding word)"),
        new("idiom_literal", "an idiom or set phrase taken literally"),
        new("made_up_word", "a made-up word or blend of words"),
        new("absurd_logic", "absurd logic or an unexpected situation, without a pun"),
        new("other", "none of the above"),
    ];

    public static string Version(string model) => $"{model}/{QuestionSet}";
}

/// <summary>
/// Calls Jev's decide API (jevtypesafeai.com) with the joke and its explanation as the state. Only joke text goes
/// there, never anything about visitors. Every answer reports its cost and the credits left, which go to
/// <see cref="SimilarityMetrics"/> (a low balance alerts before Jev stops answering). The HTTP client instrumentation
/// gives each call a span; the key is a header, never logged.
/// </summary>
public sealed class JevClient : IJevClient
{
    private readonly HttpClient http;
    private readonly IOptions<SimilarityOptions> options;
    private readonly SimilarityMetrics metrics;

    public JevClient(HttpClient http, IOptions<SimilarityOptions> options, SimilarityMetrics metrics)
    {
        this.http = http;
        this.options = options;
        this.metrics = metrics;
    }

    public async Task<float[]> ProfileAsync(string input, CancellationToken cancellationToken)
    {
        var jev = options.Value.Jev;
        using var request = new HttpRequestMessage(HttpMethod.Post, jev.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = jev.Model,
                state = input,
                questions = new Dictionary<string, object>
                {
                    ["topic"] = Question("What is this joke mostly about? (The joke is in Ukrainian.)", JevQuestions.Topics),
                    ["wordplay"] = Question("What kind of wordplay makes this joke work?", JevQuestions.Wordplay),
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jev.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            metrics.RecordJevRequest("failed");
            throw;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // 402: the credits ran out. The body says why; it's about our request (joke text), never visitors.
                metrics.RecordJevRequest((int)response.StatusCode == 402 ? "out_of_credits" : "failed");
                throw new HttpRequestException($"Jev answered {(int)response.StatusCode}: {Shorten(body)}", null, response.StatusCode);
            }

            // Counted as succeeded only once the answer is read: a 2xx with a body we can't use made no profile.
            JsonNode answer;
            float[] profile;
            try
            {
                answer = JsonNode.Parse(body) ?? throw new FormatException("Jev answered with an empty body.");
                var answers = answer["answers"] ?? throw new FormatException("Jev's answer has no answers.");
                profile =
                [
                    .. Probabilities(answers["topic"] ?? throw new FormatException("Jev's answer has no topic."), JevQuestions.Topics),
                    .. Probabilities(answers["wordplay"] ?? throw new FormatException("Jev's answer has no wordplay."), JevQuestions.Wordplay),
                ];
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                metrics.RecordJevRequest("failed");
                throw new FormatException($"Jev's answer couldn't be read: {Shorten(body)}", ex);
            }

            metrics.RecordJevRequest("succeeded");
            if (answer["usage"]?["cost_usd"]?.GetValue<double>() is { } cost)
                metrics.RecordJevCost(cost);
            if (answer["usage"]?["credits_remaining_usd"]?.GetValue<double>() is { } credits)
                metrics.RecordJevCredits(credits);
            return profile;
        }
    }

    private static object Question(string instructions, IReadOnlyList<KeyValuePair<string, string>> options)
        => new { type = "choice", instructions, criteria = options.ToDictionary(o => o.Key, o => o.Value) };

    private static IEnumerable<float> Probabilities(JsonNode answer, IReadOnlyList<KeyValuePair<string, string>> options)
    {
        var probabilities = (answer["probabilities"] ?? throw new FormatException("Jev's answer has no probabilities.")).AsObject();
        // Read now, inside the caller's try: a lazy sequence would fail later, outside it.
        return options.Select(o => probabilities[o.Key]?.GetValue<float>() ?? 0f).ToArray();
    }

    private static string Shorten(string body) => body.Length <= 500 ? body : $"{body[..500]}…";
}
