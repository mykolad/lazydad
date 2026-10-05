using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;

namespace LazyDad.Tests;

public sealed class JevClientTests : IDisposable
{
    private readonly ServiceProvider metricsProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly FakeHandler handler = new();

    public void Dispose() => metricsProvider.Dispose();

    private IMeterFactory Meters => metricsProvider.GetRequiredService<IMeterFactory>();

    private JevClient CreateClient()
        => new(new HttpClient(handler),
            Options.Create(new SimilarityOptions { Jev = { Endpoint = "https://jev.test/api/v1/decide", Model = "jev-9.9.9", ApiKey = " secret-key\n" } }),
            new SimilarityMetrics(Meters));

    private const string Answer = """
        {"model":"jev-9.9.9",
         "answers":{"topic":{"choice":"food","probabilities":{"food":0.75,"other":0.25}},
                    "wordplay":{"choice":"sound_alike","probabilities":{"sound_alike":0.5,"double_meaning":0.5}}},
         "usage":{"input_tokens":400,"cost_usd":0.0002,"credits_remaining_usd":4.25}}
        """;

    [Fact]
    public async Task ProfileAsync_AsksBothQuestions_WithThePinnedModelAndTheKey_AndReturnsProbabilitiesInQuestionOrder()
    {
        handler.Reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };

        var profile = await CreateClient().ProfileAsync("Чому кава бадьора?", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("Bearer secret-key", request.Authorization);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("jev-9.9.9", body["model"]!.GetValue<string>());
        Assert.Equal("Чому кава бадьора?", body["state"]!.GetValue<string>());
        Assert.Equal(JevQuestions.Topics.Select(t => t.Key), body["questions"]!["topic"]!["criteria"]!.AsObject().Select(p => p.Key));
        Assert.Equal(JevQuestions.Wordplay.Select(t => t.Key), body["questions"]!["wordplay"]!["criteria"]!.AsObject().Select(p => p.Key));

        Assert.Equal(JevQuestions.Topics.Count + JevQuestions.Wordplay.Count, profile.Length);
        Assert.Equal(0.75f, profile[Index(JevQuestions.Topics, "food")]);
        Assert.Equal(0.25f, profile[Index(JevQuestions.Topics, "other")]);
        Assert.Equal(0f, profile[Index(JevQuestions.Topics, "school")]);
        var wordplay = JevQuestions.Topics.Count;
        Assert.Equal(0.5f, profile[wordplay + Index(JevQuestions.Wordplay, "sound_alike")]);
        Assert.Equal(0.5f, profile[wordplay + Index(JevQuestions.Wordplay, "double_meaning")]);
    }

    [Fact]
    public async Task ProfileAsync_CountsTheRequest_ItsCost_AndTheCreditsLeft()
    {
        handler.Reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer) };
        using var requests = new MetricCollector<long>(Meters, LazyDadTelemetry.Name, "lazydad.jev.requests");
        using var cost = new MetricCollector<double>(Meters, LazyDadTelemetry.Name, "lazydad.jev.cost");
        using var credits = new MetricCollector<double>(Meters, LazyDadTelemetry.Name, "lazydad.jev.credits");

        await CreateClient().ProfileAsync("joke", CancellationToken.None);

        Assert.Equal("succeeded", Assert.Single(requests.GetMeasurementSnapshot(), m => m.Value != 0).Tags["outcome"]);
        Assert.Equal(0.0002, Assert.Single(cost.GetMeasurementSnapshot(), m => m.Value != 0).Value);
        credits.RecordObservableInstruments();
        Assert.Equal(4.25, Assert.Single(credits.GetMeasurementSnapshot()).Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "out_of_credits")]
    [InlineData(HttpStatusCode.InternalServerError, "failed")]
    public async Task ProfileAsync_WhenJevRefuses_ThrowsAndCountsTheOutcome(HttpStatusCode status, string outcome)
    {
        handler.Reply = new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"no\"}") };
        using var requests = new MetricCollector<long>(Meters, LazyDadTelemetry.Name, "lazydad.jev.requests");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient().ProfileAsync("joke", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(outcome, Assert.Single(requests.GetMeasurementSnapshot(), m => m.Value != 0).Tags["outcome"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"usage\":{\"cost_usd\":0.0002}}")]
    [InlineData("{\"answers\":{\"topic\":{\"probabilities\":{}}}}")]
    public async Task ProfileAsync_WhenTheAnswerCantBeRead_ThrowsAndCountsAFailure(string body)
    {
        handler.Reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        using var requests = new MetricCollector<long>(Meters, LazyDadTelemetry.Name, "lazydad.jev.requests");

        await Assert.ThrowsAsync<FormatException>(() => CreateClient().ProfileAsync("joke", CancellationToken.None));

        Assert.Equal("failed", Assert.Single(requests.GetMeasurementSnapshot(), m => m.Value != 0).Tags["outcome"]);
    }

    private static int Index(IReadOnlyList<KeyValuePair<string, string>> options, string key)
        => options.Select((o, i) => (o.Key, i)).Single(x => x.Key == key).i;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage Reply { get; set; } = new(HttpStatusCode.OK);
        public List<(string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Reply;
        }
    }
}
