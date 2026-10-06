using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using LazyDad.Api.Configuration;
using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace LazyDad.Tests;

/// <summary>The provider probe (OAuthCodeProbe), the service that runs it, and what it shows in the metrics.</summary>
public sealed class SignInProbeTests : IDisposable
{
    private static readonly OAuthClientOptions Client = new() { ClientId = "client-id", ClientSecret = "client-secret" };

    private readonly Answers provider = new();
    private readonly ServiceProvider services;

    public SignInProbeTests()
    {
        var collection = new ServiceCollection().AddMetrics().AddLogging();
        collection.AddHttpClient(SignInBackchannel.ClientName(SignInProviders.GitHub)).ConfigurePrimaryHttpMessageHandler(() => provider);
        collection.AddSingleton(new EnabledSignInProviders([SignInProviders.GitHub]));
        collection.AddSingleton<ISignInProbe>(services => OAuthCodeProbe.GitHub(Client, services));
        collection.AddSingleton<SignInProviderStatus>();
        collection.AddSingleton<SignInMetrics>();
        services = collection.BuildServiceProvider();
    }

    public void Dispose() => services.Dispose();

    private Task<ProviderState> ProbeAsync() => services.GetRequiredService<ISignInProbe>().ProbeAsync(CancellationToken.None);

    // GitHub answers 200 with the error in the body; standard OAuth providers answer 400 or 401.
    [Theory]
    [InlineData(HttpStatusCode.OK, """{"error": "bad_verification_code"}""", ProviderState.Valid)]
    [InlineData(HttpStatusCode.BadRequest, """{"error": "invalid_grant"}""", ProviderState.Valid)]
    [InlineData(HttpStatusCode.OK, """{"error": "incorrect_client_credentials"}""", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error": "invalid_client"}""", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.OK, """{"error": "redirect_uri_mismatch"}""", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.OK, """{"error": "something_new"}""", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.OK, """{"access_token": "never"}""", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.NotFound, "", ProviderState.Invalid)]
    [InlineData(HttpStatusCode.TooManyRequests, "", ProviderState.Unreachable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error": "bad_verification_code"}""", ProviderState.Unreachable)]
    public async Task Probe_TellsTheStateFromTheAnswer(HttpStatusCode status, string body, ProviderState expected)
    {
        provider.Answer = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        Assert.Equal(expected, await ProbeAsync());
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task Probe_WithoutAnAnswer_IsUnreachable(Type exception)
    {
        // A network error, or HttpClient's own timeout (a cancellation the caller didn't ask for).
        provider.Answer = () => throw (Exception)Activator.CreateInstance(exception)!;

        Assert.Equal(ProviderState.Unreachable, await ProbeAsync());
    }

    [Fact]
    public async Task Probe_SendsTheClientAndAMadeUpCode_ToTheTokenEndpoint()
    {
        provider.Answer = () => Json("""{"error": "bad_verification_code"}""");

        await ProbeAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Equal("https://github.com/login/oauth/access_token", request.Uri);
        Assert.Equal("client_id=client-id&client_secret=client-secret&code=lazydad-probe&grant_type=authorization_code", request.Body);
    }

    [Fact]
    public async Task Service_ProbesAtStartAndAgain_AndRecordsTheStateAndHowLongItTook()
    {
        using var calls = new MetricCollector<double>(services.GetRequiredService<IMeterFactory>(), LazyDadTelemetry.Name, "lazydad.signin.provider.duration");
        provider.Answer = () => Json("""{"error": "bad_verification_code"}""");
        var status = services.GetRequiredService<SignInProviderStatus>();
        Assert.Equal(ProviderState.Pending, status[SignInProviders.GitHub]);

        using var service = Service(TimeSpan.FromMilliseconds(10));
        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => { lock (provider.Requests) return provider.Requests.Count >= 2; });
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(ProviderState.Valid, status[SignInProviders.GitHub]);
        // Timed once per probe, by the probe service only: the back channel's own timing skips the probe's request.
        var measured = calls.GetMeasurementSnapshot();
        Assert.Equal(provider.Requests.Count, measured.Count);
        Assert.All(measured, m =>
        {
            Assert.Equal("github", m.Tags["provider"]);
            Assert.Equal(SignInBackchannel.Probe, m.Tags["operation"]);
            Assert.Equal("valid", m.Tags["outcome"]);
        });
    }

    [Fact]
    public async Task Service_SurvivesAProbeThatThrows()
    {
        var status = services.GetRequiredService<SignInProviderStatus>();
        var throws = new Throws();
        using var service = new SignInProbeService([throws], status, services.GetRequiredService<SignInMetrics>(),
            NullLogger<SignInProbeService>.Instance, TimeSpan.FromMilliseconds(10));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => Volatile.Read(ref throws.Calls) >= 2);
        await service.StopAsync(CancellationToken.None);

        // It kept probing, and stopped cleanly; the state stays as it was.
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(ProviderState.Pending, status[SignInProviders.GitHub]);
    }

    [Fact]
    public void StateGauge_ShowsEachProbedProvidersState_AndNothingForTheOthers()
    {
        services.GetRequiredService<SignInMetrics>();
        using var gauge = new MetricCollector<int>(services.GetRequiredService<IMeterFactory>(), LazyDadTelemetry.Name, "lazydad.signin.provider.state");
        var status = services.GetRequiredService<SignInProviderStatus>();

        gauge.RecordObservableInstruments();
        Assert.Empty(gauge.GetMeasurementSnapshot());   // pending: no state yet

        status.Set(SignInProviders.GitHub, ProviderState.Unreachable);
        gauge.RecordObservableInstruments();
        var measured = Assert.Single(gauge.GetMeasurementSnapshot());
        Assert.Equal(1, measured.Value);
        Assert.Equal("github", measured.Tags["provider"]);
        Assert.Equal("unreachable", measured.Tags["state"]);
    }

    [Fact]
    public void Status_ListsEveryProvider_OffUnlessProbed()
    {
        var all = services.GetRequiredService<SignInProviderStatus>().All;

        Assert.Equal(SignInProviders.All, all.Keys);
        Assert.Equal(ProviderState.Pending, all[SignInProviders.GitHub]);
        Assert.All(all.Where(p => p.Key != SignInProviders.GitHub), p => Assert.Equal(ProviderState.Off, p.Value));
    }

    [Fact]
    public void Status_ShowsTheStatesByName()
    {
        var status = services.GetRequiredService<SignInProviderStatus>();
        status.Set(SignInProviders.GitHub, ProviderState.Unreachable);

        // As /status writes it (ASP.NET Core's web defaults).
        var json = JsonSerializer.Serialize(new { providers = status.All }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("""{"providers":{"microsoft":"off","google":"off","github":"unreachable","facebook":"off","telegram":"off"}}""", json);
    }

    private SignInProbeService Service(TimeSpan interval)
        => new(services.GetServices<ISignInProbe>(), services.GetRequiredService<SignInProviderStatus>(),
            services.GetRequiredService<SignInMetrics>(), NullLogger<SignInProbeService>.Instance, interval);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Answers : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<(string Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add((request.RequestUri!.AbsoluteUri, body));
            return Answer();
        }
    }

    private sealed class Throws : ISignInProbe
    {
        public int Calls;

        public string Provider => SignInProviders.GitHub;

        public Task<ProviderState> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("A bug.");
        }
    }
}
