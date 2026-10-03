#:package Azure.Identity@1.21.0
#:property PublishAot=false

// Similarity experiment: which method finds better "you might also like" jokes? (README.md next to this file.)
//
//   A. Jev (jevtypesafeai.com): each joke gets a probability for every topic and every kind of wordplay; two jokes are
//      similar when those profiles are close.
//   B. Embeddings (text-embedding-3-small on lazydad-openai-resource): two jokes are similar when their vectors are close.
//
// It reads production's jokes from the public API, calls both services (cached in out/, so a re-run doesn't pay again),
// and writes out/compare.html: a blind comparison of both methods' top 3 for 25 random jokes. Nothing in the app or the
// database changes.
//
//   dotnet run experiments/similarity/Similarity.cs
//
// The Jev key is read from %USERPROFILE%\.lazydad\jev-key.txt (or the file JEV_KEY_FILE names) and never printed.
// Azure is called as your `az login` (Foundry User on lazydad-openai-resource).

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

const string JokesUrl = "https://lazydad.fyi/jokes";
const string JevUrl = "https://jevtypesafeai.com/api/v1/decide";
const string EmbeddingsUrl = "https://lazydad-openai-resource.cognitiveservices.azure.com/openai/deployments/text-embedding-3-small/embeddings?api-version=2024-10-21";
const int EmbeddingDimensions = 512;
const int SampleSize = 25;
const int TopK = 3;
// How much the kind of wordplay counts next to the topic in Jev's profile: its coordinates are scaled by the square root,
// so this is the weight of its terms in the cosine (dot product and norms).
const double WordplayWeight = 0.5;

var outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath("experiments/similarity/Similarity.cs"))!, "out");
Directory.CreateDirectory(outDir);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

var topics = new Dictionary<string, string>
{
    ["food"] = "food, cooking, drinks, kitchen, vegetables, fruit",
    ["school"] = "school, lessons, teachers, studying, exams, grammar",
    ["work"] = "work, office, jobs, bosses, meetings, professions",
    ["animals"] = "animals, pets, birds, insects, fish",
    ["transport"] = "transport, cars, buses, trams, trains, roads, travel",
    ["technology"] = "computers, phones, internet, gadgets, electricity",
    ["weather"] = "weather, seasons, rain, snow, sun, wind",
    ["sport"] = "sport, games, fitness, football, chess",
    ["home"] = "home, furniture, household items, chores, repairs, tools",
    ["nature"] = "nature, plants, forests, gardens, mushrooms, farming",
    ["money"] = "money, shopping, prices, banks, markets",
    ["music"] = "music, songs, instruments, singing, dancing",
    ["health"] = "health, doctors, medicine, body, sleep",
    ["places"] = "towns, cities, regions, landmarks, geography",
    ["holidays"] = "holidays, celebrations, traditions, gifts",
    ["time"] = "time, clocks, calendars, schedules, being late",
    ["clothes"] = "clothes, shoes, fashion, sewing, tailors",
    ["family"] = "family members, relatives, children, parents",
    ["art"] = "art, books, writing, painting, crafts, theatre, films",
    ["science"] = "science, maths, physics, chemistry, space",
    ["language"] = "words, letters, language, communication, letters and post",
    ["other"] = "none of the above",
};
var wordplay = new Dictionary<string, string>
{
    ["double_meaning"] = "one word or phrase read with two meanings",
    ["sound_alike"] = "words that sound alike or share a root (a homophone, near-homophone or similar-sounding word)",
    ["idiom_literal"] = "an idiom or set phrase taken literally",
    ["made_up_word"] = "a made-up word or blend of words",
    ["absurd_logic"] = "absurd logic or an unexpected situation, without a pun",
    ["other"] = "none of the above",
};

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("LazyDadSimilarityExperiment/1.0");

// 1. Production's jokes (the public API: text, explanation, votes; nothing about visitors).
var jokes = (await http.GetFromJsonAsync<List<Joke>>(JokesUrl, json))!
    .Where(j => !string.IsNullOrWhiteSpace(j.Text)).OrderBy(j => j.Id).ToList();
Console.WriteLine($"{jokes.Count} jokes.");
// What both methods see: the joke and, if it has one, why it's funny.
string Input(Joke j) => string.IsNullOrWhiteSpace(j.Explanation) ? j.Text : $"{j.Text}\n\n(Why it's funny: {j.Explanation})";

// 2. A: Jev's topic and wordplay probabilities, per joke (cached).
var jevPath = Path.Combine(outDir, "jev.json");
var jev = File.Exists(jevPath) ? JsonSerializer.Deserialize<Dictionary<int, JevAnswer>>(File.ReadAllText(jevPath), json)! : [];
var missing = jokes.Where(j => !jev.ContainsKey(j.Id)).ToList();
if (missing.Count > 0)
{
    var keyFile = Environment.GetEnvironmentVariable("JEV_KEY_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lazydad", "jev-key.txt");
    if (!File.Exists(keyFile))
    {
        Console.Error.WriteLine($"No Jev key: put it in {keyFile} (or point JEV_KEY_FILE at a file).");
        return 1;
    }
    var key = File.ReadAllText(keyFile).Trim();
    var cost = 0.0;
    var done = 0;
    using var gate = new SemaphoreSlim(6);
    await Task.WhenAll(missing.Select(async joke =>
    {
        await gate.WaitAsync();
        try
        {
            var answer = await JevAsync(http, key, Input(joke), topics, wordplay);
            lock (jev)
            {
                jev[joke.Id] = answer;
                cost += answer.CostUsd;
                if (++done % 100 == 0)
                {
                    Console.WriteLine($"Jev: {done} of {missing.Count} (${cost:F4} so far)");
                    File.WriteAllText(jevPath, JsonSerializer.Serialize(jev, json));
                }
            }
        }
        finally { gate.Release(); }
    }));
    File.WriteAllText(jevPath, JsonSerializer.Serialize(jev, json));
    Console.WriteLine($"Jev: {missing.Count} jokes classified, ${cost:F4}.");
}

// 3. B: embeddings, per joke (cached).
var embPath = Path.Combine(outDir, "embeddings.json");
var embeddings = File.Exists(embPath) ? JsonSerializer.Deserialize<Dictionary<int, float[]>>(File.ReadAllText(embPath), json)! : [];
var toEmbed = jokes.Where(j => !embeddings.ContainsKey(j.Id)).ToList();
if (toEmbed.Count > 0)
{
    var credential = new DefaultAzureCredential();
    foreach (var batch in toEmbed.Chunk(100))
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, EmbeddingsUrl)
        {
            Content = JsonContent.Create(new { input = batch.Select(Input).ToArray(), dimensions = EmbeddingDimensions }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await SendWithRetriesAsync(http, request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        foreach (var item in body["data"]!.AsArray())
            embeddings[batch[item!["index"]!.GetValue<int>()].Id] = item["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
        // Saved after every batch, so a later failure keeps what's already paid for.
        File.WriteAllText(embPath, JsonSerializer.Serialize(embeddings, json));
        Console.WriteLine($"Embeddings: {embeddings.Count} of {jokes.Count}");
    }
}

// 4. Each method's top 3 for every joke.
var jevVectors = jokes.ToDictionary(j => j.Id, j => JevVector(jev[j.Id], topics, wordplay));
var byId = jokes.ToDictionary(j => j.Id);
List<int> Nearest(int id, Func<int, double[]> vector) => jokes.Where(j => j.Id != id)
    .Select(j => (j.Id, Score: Cosine(vector(id), vector(j.Id))))
    .OrderByDescending(x => x.Score).Take(TopK).Select(x => x.Id).ToList();
double[] Embedding(int id) => embeddings[id].Select(v => (double)v).ToArray();

// 5. The blind comparison: 25 random jokes (a fixed seed, so a re-run shows the same ones), each method's list shown as
// X or Y at random. The page keeps the answers in the browser and reveals the score at the end.
var random = new Random(20261003);
var sample = jokes.OrderBy(_ => random.Next()).Take(SampleSize).ToList();
var rounds = sample.Select(joke =>
{
    var aIsX = random.Next(2) == 0;
    var a = Nearest(joke.Id, id => jevVectors[id]);
    var b = Nearest(joke.Id, Embedding);
    return new
    {
        joke = Brief(joke, jev[joke.Id]),
        x = (aIsX ? a : b).Select(id => Brief(byId[id], jev[id])),
        y = (aIsX ? b : a).Select(id => Brief(byId[id], jev[id])),
        xIs = aIsX ? "Jev" : "Embeddings",
        overlap = a.Intersect(b).Count(),
    };
}).ToList();
var data = JsonSerializer.Serialize(rounds, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Default });
var page = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath("experiments/similarity/Similarity.cs"))!, "compare.html"))
    .Replace("/*DATA*/[]", data);
File.WriteAllText(Path.Combine(outDir, "compare.html"), page);

var topicCounts = jev.Values.GroupBy(a => a.Topic).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
Console.WriteLine($"Topics: {string.Join(", ", topicCounts)}");
Console.WriteLine($"Both methods agree on {rounds.Average(r => r.overlap):F1} of {TopK} suggestions on average.");
Console.WriteLine($"Open {Path.Combine(outDir, "compare.html")}");
return 0;

static object Brief(Joke j, JevAnswer a) => new { j.Id, j.Text, a.Topic, a.Wordplay };

static async Task<JevAnswer> JevAsync(HttpClient http, string key, string input,
    Dictionary<string, string> topics, Dictionary<string, string> wordplay)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, JevUrl)
    {
        Content = JsonContent.Create(new
        {
            state = input,
            questions = new Dictionary<string, object>
            {
                ["topic"] = new { type = "choice", instructions = "What is this joke mostly about? (The joke is in Ukrainian.)", criteria = topics },
                ["wordplay"] = new { type = "choice", instructions = "What kind of wordplay makes this joke work?", criteria = wordplay },
            },
        }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    using var response = await SendWithRetriesAsync(http, request);
    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    var answers = body["answers"]!;
    Dictionary<string, double> Probabilities(string question) => answers[question]!["probabilities"]!.AsObject()
        .ToDictionary(p => p.Key, p => p.Value!.GetValue<double>());
    return new JevAnswer(
        answers["topic"]!["choice"]!.GetValue<string>(), Probabilities("topic"),
        answers["wordplay"]!["choice"]!.GetValue<string>(), Probabilities("wordplay"),
        body["usage"]?["cost_usd"]?.GetValue<double>() ?? 0);
}

// 429 and 5xx get up to 4 more tries, with a growing pause; anything else fails the run (the cache keeps what's done).
static async Task<HttpResponseMessage> SendWithRetriesAsync(HttpClient http, HttpRequestMessage request)
{
    var content = await request.Content!.ReadAsStringAsync();
    for (var attempt = 1; ; attempt++)
    {
        using var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        copy.Headers.Authorization = request.Headers.Authorization;
        var response = await http.SendAsync(copy);
        if (response.IsSuccessStatusCode)
            return response;
        var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
        if (!retryable || attempt == 5)
            throw new HttpRequestException($"{request.RequestUri!.Host} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        response.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
    }
}

static double[] JevVector(JevAnswer a, Dictionary<string, string> topics, Dictionary<string, string> wordplay)
    => topics.Keys.Select(k => a.TopicProbabilities.GetValueOrDefault(k))
        .Concat(wordplay.Keys.Select(k => Math.Sqrt(WordplayWeight) * a.WordplayProbabilities.GetValueOrDefault(k))).ToArray();

static double Cosine(double[] a, double[] b)
{
    double dot = 0, na = 0, nb = 0;
    for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
    return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
}

record Joke(int Id, string Text, string? Explanation, int Up, int Down);
record JevAnswer(string Topic, Dictionary<string, double> TopicProbabilities, string Wordplay, Dictionary<string, double> WordplayProbabilities, double CostUsd);
