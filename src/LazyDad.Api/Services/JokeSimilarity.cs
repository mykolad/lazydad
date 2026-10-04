using LazyDad.Data.Entities;

namespace LazyDad.Api.Services;

/// <summary>
/// "You might also like" on a joke's page: the jokes in its language that share the most words with it.
/// A stand-in until the similarity experiment (experiments/similarity) picks a method: words are compared by their first
/// five letters (a rough stem: "вареник" and "вареники" match), the template words every joke repeats ("Чому … став …?
/// Бо завжди …") don't count, and ties go to the better-voted, then the newer joke. With nothing in common, the
/// best-voted jokes fill the list, so it's never empty.
/// </summary>
public static class JokeSimilarity
{
    private const int StemLength = 5;
    private const int MinWordLength = 4;

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

    /// <summary>A joke with its stems, computed once per load of the index.</summary>
    public sealed record Entry(Joke Joke, IReadOnlySet<string> Stems);

    public static List<Entry> Index(IEnumerable<Joke> jokes) => jokes.Select(j => new Entry(j, Stems(j.Text))).ToList();

    /// <summary>Up to <paramref name="limit"/> jokes like <paramref name="joke"/>, most similar first; never the joke itself.</summary>
    public static List<Joke> Similar(Joke joke, IReadOnlyList<Entry> index, int limit)
    {
        var stems = Stems(joke.Text);
        return index
            .Where(e => e.Joke.Id != joke.Id && string.Equals(e.Joke.Language, joke.Language, StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Joke, Score: Overlap(stems, e.Stems)))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Joke.Up - x.Joke.Down)
            .ThenByDescending(x => x.Joke.GeneratedAt)
            .ThenByDescending(x => x.Joke.Id)
            .Take(limit)
            .Select(x => x.Joke)
            .ToList();
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
