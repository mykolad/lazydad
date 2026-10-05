using LazyDad.Api.Services;
using LazyDad.Data.Entities;

namespace LazyDad.Tests;

public class JokeSimilarityTests
{
    private static readonly Dictionary<int, float[]> None = [];

    private static Joke MakeJoke(int id, string text, int up) => new()
    {
        Id = id,
        Language = "Ukrainian",
        Text = text,
        Up = up,
        GeneratedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddHours(id),
    };

    /// <summary>A Jev profile: one topic at full probability (by index), and one kind of wordplay.</summary>
    private static float[] Jev(int topic, int wordplay)
    {
        var profile = new float[JevQuestions.Topics.Count + JevQuestions.Wordplay.Count];
        profile[topic] = 1;
        profile[JevQuestions.Topics.Count + wordplay] = 1;
        return profile;
    }

    [Fact]
    public void Stems_MatchAcrossEndings_KeepApostrophes_AndSkipTheJokesTemplate()
    {
        var stems = JokeSimilarity.Stems("Чому вареники стали м’ясом? Бо завжди любили п'ять ВАРЕНИКІВ!");

        // "варен" once for both forms; "м’ясом" and "п'ять" stay whole words; "чому", "стали", "завжди" and "любили" are
        // the template, and "бо" is too short.
        Assert.Equal(["варен", "мясом", "пять"], stems.Order());
    }

    [Fact]
    public void Similar_WithJevProfiles_RanksByTopicAndWordplay_WordplayAtHalfWeight()
    {
        var joke = MakeJoke(1, "a", 0);
        List<Joke> all = [joke, MakeJoke(2, "b", 0), MakeJoke(3, "c", 0), MakeJoke(4, "d", 50)];
        var jev = new Dictionary<int, float[]>
        {
            [1] = Jev(topic: 0, wordplay: 0),
            [2] = Jev(topic: 0, wordplay: 1), // same topic, other wordplay
            [3] = Jev(topic: 1, wordplay: 0), // same wordplay, other topic: weighs less
            [4] = Jev(topic: 2, wordplay: 2), // nothing in common, however well voted
        };

        var result = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all, jev, None), 3);

        Assert.Equal(JokeSimilarity.JevMethod, result.Method);
        Assert.Equal([2, 3, 4], result.Jokes.Select(j => j.Id));
    }

    [Fact]
    public void Similar_WithoutAJevProfile_FallsBackToEmbeddings()
    {
        var joke = MakeJoke(1, "a", 0);
        List<Joke> all = [joke, MakeJoke(2, "b", 0), MakeJoke(3, "c", 0)];
        var jev = new Dictionary<int, float[]> { [2] = Jev(0, 0), [3] = Jev(0, 0) };
        var embeddings = new Dictionary<int, float[]> { [1] = [1, 0], [2] = [0, 1], [3] = [0.9f, 0.1f] };

        var result = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all, jev, embeddings), 2);

        Assert.Equal(JokeSimilarity.EmbeddingMethod, result.Method);
        Assert.Equal([3, 2], result.Jokes.Select(j => j.Id));
    }

    [Fact]
    public void Similar_FillsWithJokesWithoutTheProfile_BySharedWords()
    {
        // The backfill hasn't reached jokes 3 and 4 yet: they come after the profiled one, by shared words.
        var joke = MakeJoke(1, "Кава на роботі.", 0);
        List<Joke> all = [joke, MakeJoke(2, "Сир.", 0), MakeJoke(3, "Хліб.", 0), MakeJoke(4, "Кава вранці.", 0)];
        var jev = new Dictionary<int, float[]> { [1] = Jev(0, 0), [2] = Jev(0, 0) };

        var result = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all, jev, None), 3);

        Assert.Equal(JokeSimilarity.JevMethod, result.Method);
        Assert.Equal([2, 4, 3], result.Jokes.Select(j => j.Id));
    }

    [Fact]
    public void Similar_WithoutProfiles_RanksBySharedWords_ThenByVotes_ThenNewest()
    {
        var joke = MakeJoke(1, "Кава і чай пішли на роботу.", 0);
        List<Joke> all =
        [
            joke,
            MakeJoke(2, "Робота без кави — не робота.", 0),
            MakeJoke(3, "Кава любить роботу, а чай — відпочинок.", 0),
            MakeJoke(4, "Сир пішов у кіно.", 9),
            MakeJoke(5, "Хліб пішов у театр.", 9),
        ];

        var result = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all, None, None), 4);

        // 3 shares "кава" and "робот", 2 only "робот"; 4 and 5 share nothing and tie on votes: the newer first.
        Assert.Equal(JokeSimilarity.WordsMethod, result.Method);
        Assert.Equal([3, 2, 5, 4], result.Jokes.Select(j => j.Id));
    }

    [Fact]
    public void Similar_StaysInTheJokesLanguage_AndRespectsTheLimit()
    {
        var joke = MakeJoke(1, "Кава на роботі.", 0);
        var english = MakeJoke(2, "Кава на роботі.", 0);
        english.Language = "English";
        List<Joke> all = [joke, english, MakeJoke(3, "Сир.", 0), MakeJoke(4, "Хліб.", 0)];

        var result = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all, None, None), 1);

        Assert.Equal([4], result.Jokes.Select(j => j.Id));
    }
}
