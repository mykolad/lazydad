using System.Net;
using LazyDad.Api.Controllers;
using LazyDad.Api.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LazyDad.Tests;

public class VoteRateLimitTests
{
    [Fact]
    public async Task TheConfiguredLimit_RejectsTheNextVoteFromTheSameAddress()
    {
        var statuses = await PostVotesAsync(3, new Dictionary<string, string?> { [VoteRateLimit.ConfigurationKey] = "2" });

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task WithoutTheSetting_ThirtyVotesAMinutePass()
    {
        var statuses = await PostVotesAsync(VoteRateLimit.DefaultPerMinute + 1, new Dictionary<string, string?>());

        Assert.All(statuses[..VoteRateLimit.DefaultPerMinute], status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public void ALimitBelowOne_IsRefusedAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [VoteRateLimit.ConfigurationKey] = "0" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddVoteRateLimit(configuration));
        Assert.Contains(VoteRateLimit.ConfigurationKey, error.Message);
    }

    // An app with only the limiter and a vote-like endpoint behind the vote policy, called from one address.
    private static async Task<HttpStatusCode[]> PostVotesAsync(int count, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddVoteRateLimit(builder.Configuration);
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapPost("/vote", () => "ok").RequireRateLimiting(JokesController.VotePolicy);
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var statuses = new HttpStatusCode[count];
        for (var i = 0; i < count; i++)
            statuses[i] = (await http.PostAsync("/vote", content: null)).StatusCode;
        await app.StopAsync();
        return statuses;
    }
}
