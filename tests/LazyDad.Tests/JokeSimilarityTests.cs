using LazyDad.Api.Services;
using LazyDad.Data.Entities;

namespace LazyDad.Tests;

public class JokeSimilarityTests
{
    private static Joke MakeJoke(int id, string text, int up) => new()
    {
        Id = id,
        Language = "Ukrainian",
        Text = text,
        Up = up,
        GeneratedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc).AddHours(id),
    };

    [Fact]
    public void Stems_MatchAcrossEndings_KeepApostrophes_AndSkipTheJokesTemplate()
    {
        var stems = JokeSimilarity.Stems("Чому вареники стали м’ясом? Бо завжди любили п'ять ВАРЕНИКІВ!");

        // "варен" once for both forms; "м’ясом" and "п'ять" stay whole words; "чому", "стали", "завжди" and "любили" are
        // the template, and "бо" is too short.
        Assert.Equal(["варен", "мясом", "пять"], stems.Order());
    }

    [Fact]
    public void Similar_RanksBySharedWords_ThenByVotes_ThenNewest()
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

        var similar = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all), 4);

        // 3 shares "кава" and "робот", 2 only "робот"; 4 and 5 share nothing and tie on votes: the newer first.
        Assert.Equal([3, 2, 5, 4], similar.Select(j => j.Id));
    }

    [Fact]
    public void Similar_StaysInTheJokesLanguage_AndRespectsTheLimit()
    {
        var joke = MakeJoke(1, "Кава на роботі.", 0);
        var english = MakeJoke(2, "Кава на роботі.", 0);
        english.Language = "English";
        List<Joke> all = [joke, english, MakeJoke(3, "Сир.", 0), MakeJoke(4, "Хліб.", 0)];

        var similar = JokeSimilarity.Similar(joke, JokeSimilarity.Index(all), 1);

        Assert.Equal([4], similar.Select(j => j.Id));
    }
}
