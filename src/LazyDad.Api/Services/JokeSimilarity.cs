using LazyDad.Data.Entities;

namespace LazyDad.Api.Services;

/// <summary>
/// "You might also like" on a joke's page, ranked from the stored profiles (JokeProfiler), with no calls at request time:
/// <list type="number">
/// <item><b>Jev</b> (the main method, the winner of the similarity experiment's blind test): the cosine of the jokes'
/// topic and wordplay probabilities, wordplay at half weight. It finds jokes in the same style rather than about the same
/// thing.</item>
/// <item><b>Embeddings</b>, when the joke has no Jev profile (Jev out of credits or down when it was saved).</item>
/// <item><b>Shared words</b>, when it has neither yet (a joke saved moments ago, before its tick profiled it): words
/// compared by their first five letters ("вареник" and "вареники" match), without the template words every joke repeats
/// ("Чому … став …? Бо завжди …").</item>
/// </list>
/// Only jokes with the chosen method's profile compete; ties go to the better-voted, then the newer joke. Shared words
/// fill whatever is left, and with nothing in common, the best-voted jokes do, so the list is never short.
/// Comparing one joke with all of them is N small dot products per request (and requests share the cached index).
/// </summary>
public static class JokeSimilarity
{
    private const int StemLength = 5;
    private const int MinWordLength = 4;
    // The weight of the wordplay terms next to the topic ones in the Jev cosine (dot product and norms). Their
    // coordinates are scaled by its square root, as in the experiment.
    private const double WordplayWeight = 0.5;

    // Stems (first five letters) of words that say nothing about a joke's topic: the jokes' own template, pronouns,
    // auxiliaries and fillers. Words shorter than four letters are skipped anyway.
    private static readonly HashSet<string> StopStems =
    [
        "чому", "тому", "став", "стала", "стали", "стало", "завжд", "добре", "найкр", "вмів", "вміла", "вміли", "вміє", "вміют",
        "любив", "любил", "любит", "знав", "знала", "знали", "знає", "знают", "мала", "мали", "має", "мають", "мают",
        "тато", "тата", "тату", "татов", "татко", "батьк", "сина", "синок", "сказа", "каже", "кажу", "відпо", "питає", "запит",
        "коли", "який", "якщо", "тільк", "навіт", "також", "зараз", "потім", "дуже", "мене", "тебе", "себе", "його", "вона",
        "вони", "цього", "цієї", "буде", "було", "була", "були", "може", "можна", "треба", "через", "після", "перед", "своє",
        "свою", "свій", "свої", "своїм", "своєю", "весь", "всіх", "усіх", "один", "одна", "одне", "ніби", "наче", "прост",
        "хоче", "хотів", "хотіл", "думав", "думає", "знову", "тепер", "навіщ", "адже", "щоби", "хоча", "аніж", "нього", "неї",
    ];

    public const string JevMethod = "jev";
    public const string EmbeddingMethod = "embedding";
    public const string WordsMethod = "words";

    /// <summary>A joke with its stems and whichever profiles it has, worked out once per load of the index.</summary>
    public sealed record Entry(Joke Joke, IReadOnlySet<string> Stems, float[]? Jev, float[]? Embedding);

    /// <summary>The suggestions, and the method that ranked them (for the metrics).</summary>
    public sealed record Result(List<Joke> Jokes, string Method);

    /// <param name="jev">Jev profiles by joke id, in <see cref="JevQuestions"/> order (topics, then wordplay).</param>
    /// <param name="embeddings">Embeddings by joke id.</param>
    public static List<Entry> Index(IEnumerable<Joke> jokes, IReadOnlyDictionary<int, float[]> jev, IReadOnlyDictionary<int, float[]> embeddings)
        => jokes.Select(j => new Entry(j, Stems(j.Text),
                jev.TryGetValue(j.Id, out var profile) ? WeighWordplay(profile) : null,
                embeddings.GetValueOrDefault(j.Id)))
            .ToList();

    /// <summary>Up to <paramref name="limit"/> jokes like <paramref name="joke"/>, most similar first; never the joke itself.</summary>
    public static Result Similar(Joke joke, IReadOnlyList<Entry> index, int limit)
    {
        var target = index.FirstOrDefault(e => e.Joke.Id == joke.Id) ?? new Entry(joke, Stems(joke.Text), null, null);
        var candidates = index
            .Where(e => e.Joke.Id != joke.Id && string.Equals(e.Joke.Language, joke.Language, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The best method both this joke and some others have a profile for.
        var (method, score) =
            target.Jev is { } jev && candidates.Any(c => c.Jev is not null)
                ? (JevMethod, (Func<Entry, double?>)(c => c.Jev is { } other ? Cosine(jev, other) : null))
                : target.Embedding is { } embedding && candidates.Any(c => c.Embedding is not null)
                    ? (EmbeddingMethod, c => c.Embedding is { } other ? Cosine(embedding, other) : null)
                    : (WordsMethod, (Func<Entry, double?>)(c => Overlap(target.Stems, c.Stems)));

        var ranked = Rank(candidates, score).Take(limit).ToList();
        if (ranked.Count < limit && method != WordsMethod)
        {
            // Jokes without that profile yet (the backfill hasn't reached them) fill the rest by shared words.
            var taken = ranked.Select(j => j.Id).ToHashSet();
            ranked.AddRange(Rank(candidates.Where(c => !taken.Contains(c.Joke.Id)), c => Overlap(target.Stems, c.Stems)).Take(limit - ranked.Count));
        }
        return new Result(ranked, method);
    }

    private static IEnumerable<Joke> Rank(IEnumerable<Entry> candidates, Func<Entry, double?> score)
        => candidates
            .Select(e => (e.Joke, Score: score(e)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Joke.Up - x.Joke.Down)
            .ThenByDescending(x => x.Joke.GeneratedAt)
            .ThenByDescending(x => x.Joke.Id)
            .Select(x => x.Joke);

    private static float[] WeighWordplay(float[] profile)
    {
        var weighted = (float[])profile.Clone();
        var scale = (float)Math.Sqrt(WordplayWeight);
        for (var i = JevQuestions.Topics.Count; i < weighted.Length; i++)
            weighted[i] *= scale;
        return weighted;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    internal static HashSet<string> Stems(string text)
    {
        var stems = new HashSet<string>(StringComparer.Ordinal);
        // An apostrophe is part of a Ukrainian word (м'ясо, п’ять), so it's dropped rather than splitting the word.
        var letters = text.ToLowerInvariant().Replace("'", "").Replace("’", "").Replace("ʼ", "");
        var start = 0;
        for (var i = 0; i <= letters.Length; i++)
        {
            if (i < letters.Length && char.IsLetter(letters[i]))
                continue;
            var length = i - start;
            if (length >= MinWordLength)
            {
                var stem = letters.Substring(start, Math.Min(length, StemLength));
                if (!StopStems.Contains(stem))
                    stems.Add(stem);
            }
            start = i + 1;
        }
        return stems;
    }

    // Cosine of the two stem sets: shared stems over the geometric mean of their sizes, so long jokes don't win by length.
    private static double Overlap(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var shared = a.Count(b.Contains);
        return shared / Math.Sqrt(a.Count * (double)b.Count);
    }
}
