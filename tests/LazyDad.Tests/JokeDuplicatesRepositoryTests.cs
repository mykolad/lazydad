using LazyDad.Data;
using LazyDad.Data.Entities;
using LazyDad.Data.Repositories;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LazyDad.Tests;

/// <summary>
/// The same joke saved twice: <see cref="JokeText"/>, and the repository's check and cleanup, against a real database
/// (SQLite in memory, or SQL Server with the migrations in CI; see TestDatabase), so the cascades are real.
/// </summary>
public sealed class JokeDuplicatesRepositoryTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase database = new();

    public void Dispose() => database.Dispose();

    private LazyDadDbContext CreateContext() => database.CreateContext();

    private int Add(string text, int daysLater, int up = 0, string language = "Ukrainian", bool withHash = true)
    {
        using var context = CreateContext();
        var joke = new Joke { Language = language, Model = "gpt-5.3-chat", Text = text, GeneratedAt = Start.AddDays(daysLater), Up = up, TextHash = withHash ? JokeText.Hash(text) : null };
        context.Jokes.Add(joke);
        context.SaveChanges();
        return joke.Id;
    }

    private List<int> RemainingIds()
    {
        using var context = CreateContext();
        return context.Jokes.OrderBy(j => j.Id).Select(j => j.Id).ToList();
    }

    [Theory]
    [InlineData("Чому гречка стала бухгалтеркою? Бо рахувала крупні витрати!", "чому гречка стала бухгалтеркою бо рахувала крупні витрати.")]
    [InlineData("М'ясо — «добре».", "Мʼясо добре")]
    [InlineData("— Тату?\n— Так!", "Тату? Так")]
    public void Key_IgnoresCasePunctuationQuotesApostrophesAndSpacing(string a, string b)
        => Assert.Equal(JokeText.Key(a), JokeText.Key(b));

    [Fact]
    public void Key_TellsDifferentWordsApart()
        => Assert.NotEqual(JokeText.Key("Бо рахувала витрати"), JokeText.Key("Бо рахувала прибутки"));

    [Fact]
    public async Task TextExistsAsync_FindsTheSameJokeWithOtherPunctuation_InTheSameLanguageOnly()
    {
        Add("Чому кефір став детективом? Бо вмів розквасити справу!", 0);
        await using var context = CreateContext();
        var repository = new JokeRepository(context);

        Assert.True(await repository.TextExistsAsync("Ukrainian", "чому кефір став детективом бо вмів розквасити справу.", CancellationToken.None));
        Assert.False(await repository.TextExistsAsync("Ukrainian", "Чому кефір став суддею? Бо вмів розквасити справу!", CancellationToken.None));
        Assert.False(await repository.TextExistsAsync("English", "Чому кефір став детективом? Бо вмів розквасити справу!", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_KeepsTheOldestCopy_AndTheirProfilesAndVotesGoWithTheOthers()
    {
        var oldest = Add("Чому гречка стала бухгалтеркою? Бо рахувала крупні витрати!", 0);
        var later = Add("Чому гречка стала бухгалтеркою? Бо рахувала крупні витрати.", 10);
        var latest = Add("чому гречка стала бухгалтеркою? бо рахувала крупні витрати!", 20);
        var other = Add("Чому кефір став детективом? Бо вмів розквасити справу!", 5);
        var english = Add("Чому гречка стала бухгалтеркою? Бо рахувала крупні витрати!", 30, language: "English");
        await using (var context = CreateContext())
        {
            context.JokeProfiles.Add(new JokeProfile { JokeId = later, Kind = JokeProfile.JevKind, Version = "v1", Vector = [1], CreatedAt = Start });
            context.Votes.Add(new Vote { JokeId = later, VoterKey = new byte[Vote.VoterKeyLength], Value = 1, UpdatedAt = Start });
            await context.SaveChangesAsync();
        }

        IReadOnlyList<int> removed;
        await using (var context = CreateContext())
            removed = await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None);

        Assert.Equal([later, latest], removed.Order());
        Assert.Equal([oldest, other, english], RemainingIds());
        await using var verify = CreateContext();
        Assert.Empty(await verify.JokeProfiles.ToListAsync());
        Assert.Empty(await verify.Votes.ToListAsync());
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_KeepsTheMostVotedCopy()
    {
        var oldest = Add("Чому узвар став програмістом? Бо компотував файли!", 0);
        var voted = Add("Чому узвар став програмістом? Бо компотував файли.", 10, up: 3);

        await using (var context = CreateContext())
            await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None);

        Assert.Equal([voted], RemainingIds());
        Assert.DoesNotContain(oldest, RemainingIds());
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_NeverDeletesATopThreeJoke()
    {
        var first = Add("Чому сало стало бібліотекарем? Бо знало найтовстіший том!", 0);
        var topOne = Add("Чому сало стало бібліотекарем? Бо знало найтовстіший том.", 10);
        var topTwo = Add("Чому сало стало бібліотекарем? Бо знало найтовстіший том!", 20);
        await using (var context = CreateContext())
        {
            context.TopJokes.AddRange(
                new TopJoke { Language = "Ukrainian", Rank = 1, JokeId = topOne, Reason = "r", JudgeModel = "j", SelectedAt = Start },
                new TopJoke { Language = "Ukrainian", Rank = 2, JokeId = topTwo, Reason = "r", JudgeModel = "j", SelectedAt = Start });
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
            await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None);

        // The Top 3 copies win over the oldest, and a second Top 3 copy stays too, so no slot is lost.
        Assert.Equal([topOne, topTwo], RemainingIds());
        Assert.DoesNotContain(first, RemainingIds());
        await using var verify = CreateContext();
        Assert.Equal(2, await verify.TopJokes.CountAsync());
    }

    [Fact]
    public async Task GetExistingIdsAsync_ReturnsOnlyTheIdsThatAreStillJokes()
    {
        var kept = Add("Перший жарт.", 0);

        await using var context = CreateContext();
        Assert.Equal([kept], await new JokeRepository(context).GetExistingIdsAsync([kept, kept + 1000], CancellationToken.None));
        Assert.Empty(await new JokeRepository(context).GetExistingIdsAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_WithoutDuplicates_DeletesNothing()
    {
        Add("Перший жарт.", 0);
        Add("Другий жарт.", 1);

        await using var context = CreateContext();
        Assert.Empty(await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None));
        Assert.Equal(2, RemainingIds().Count);
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_SparesACopyPromotedToTheTopThreeWhileItRan()
    {
        var oldest = Add("Чому дерун став водієм? Бо тримався тертого шляху!", 0);
        var promoted = Add("Чому дерун став водієм? Бо тримався тертого шляху.", 10);
        // Another replica's tick puts the later copy in the Top 3 just before this delete runs.
        var promotion = new BeforeDelete(() =>
        {
            using var other = CreateContext();
            other.TopJokes.Add(new TopJoke { Language = "Ukrainian", Rank = 1, JokeId = promoted, Reason = "r", JudgeModel = "j", SelectedAt = Start });
            other.SaveChanges();
        });

        IReadOnlyList<int> removed;
        await using (var context = database.CreateContext([promotion]))
            removed = await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None);

        Assert.True(promotion.Ran);
        Assert.Empty(removed);
        Assert.Equal([oldest, promoted], RemainingIds());
        await using var verify = CreateContext();
        Assert.Equal(1, await verify.TopJokes.CountAsync());
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_SparesACopyThatGotAVoteWhileItRan()
    {
        var oldest = Add("Чому борщ став фотографом? Бо ловив бурякові кадри!", 0);
        var voted = Add("Чому борщ став фотографом? Бо ловив бурякові кадри.", 10);
        // A visitor votes for the later copy just before this delete runs: it's now the most-voted, so it stays.
        var vote = new BeforeDelete(() =>
        {
            using var other = CreateContext();
            other.Jokes.Where(j => j.Id == voted).ExecuteUpdate(s => s.SetProperty(j => j.Up, 1));
        });

        IReadOnlyList<int> removed;
        await using (var context = database.CreateContext([vote]))
            removed = await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", null, CancellationToken.None);

        Assert.True(vote.Ran);
        Assert.Empty(removed);
        Assert.Equal([oldest, voted], RemainingIds());
    }

    private sealed class BeforeDelete(Action action) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
        {
            if (!Ran && command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Ran = true;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public void Hash_IsTheSameForTheSameJoke_AndDiffersOtherwise()
    {
        Assert.Equal(JokeText.Hash("Бо рахувала витрати!"), JokeText.Hash("бо рахувала витрати."));
        Assert.NotEqual(JokeText.Hash("Бо рахувала витрати"), JokeText.Hash("Бо рахувала прибутки"));
        Assert.Equal(JokeText.HashLength, JokeText.Hash("x").Length);
    }

    [Fact]
    public async Task FillTextHashesAsync_GivesTheLanguagesJokesWithoutOneTheirHash_AndAddAsyncSetsIt()
    {
        var old = Add("Старий жарт!", 0, withHash: false);
        var english = Add("Old joke!", 0, language: "English", withHash: false);
        Add("Новіший жарт!", 1);
        await using (var context = CreateContext())
            await new JokeRepository(context).AddAsync(new Joke { Language = "Ukrainian", Model = "m", Text = "Новий жарт!", GeneratedAt = Start }, CancellationToken.None);

        int filled;
        await using (var context = CreateContext())
            filled = await new JokeRepository(context).FillTextHashesAsync("Ukrainian", CancellationToken.None);

        Assert.Equal(1, filled);
        await using var verify = CreateContext();
        Assert.All(await verify.Jokes.Where(j => j.Language == "Ukrainian").ToListAsync(), j => Assert.Equal(JokeText.Hash(j.Text), j.TextHash));
        Assert.Null((await verify.Jokes.SingleAsync(j => j.Id == english)).TextHash);
        Assert.True(await new JokeRepository(verify).TextExistsAsync("Ukrainian", "старий жарт", CancellationToken.None));
        Assert.Equal(0, await new JokeRepository(verify).FillTextHashesAsync("Ukrainian", CancellationToken.None));
        Assert.Contains(old, RemainingIds());
    }

    [Fact]
    public async Task RemoveDuplicatesAsync_Since_LooksOnlyAtCopiesOfJokesSavedSinceThen()
    {
        // An old pair nobody saved lately, and a joke with a fresh copy.
        var oldA = Add("Старий жарт!", 0);
        var oldB = Add("Старий жарт.", 1);
        var original = Add("Відомий жарт!", 2);
        var freshCopy = Add("Відомий жарт.", 30);

        IReadOnlyList<int> removed;
        await using (var context = CreateContext())
            removed = await new JokeRepository(context).RemoveDuplicatesAsync("Ukrainian", Start.AddDays(29), CancellationToken.None);

        Assert.Equal([freshCopy], removed);
        Assert.Equal([oldA, oldB, original], RemainingIds());
    }
}
