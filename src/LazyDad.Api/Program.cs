using LazyDad.Api.Configuration;
using LazyDad.Api.Networking;
using LazyDad.Api.Services;
using LazyDad.Api.SignIn;
using LazyDad.Api.Telemetry;
using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
// Traces, metrics and logs to Grafana Cloud, when OTEL_EXPORTER_OTLP_ENDPOINT is set (see TelemetryExtensions).
builder.AddTelemetry();

// Container Apps' ingress is the only way in; it appends the caller's address to X-Forwarded-For. Behind Cloudflare
// that caller is a Cloudflare edge server; CloudflareClientAddressMiddleware then takes the visitor from CF-Connecting-IP.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
// Votes are anonymous, so at least cap how fast one address can cast them (RateLimiting:VotesPerMinute).
builder.Services.AddVoteRateLimit(builder.Configuration);

// A connect timeout long enough for a paused serverless database to resume (see SqlConnectionStrings).
builder.Services.AddDbContext<LazyDadDbContext>(options =>
    options.UseSqlServer(SqlConnectionStrings.WithResumeTimeout(builder.Configuration.GetConnectionString("DefaultConnection") ?? ""),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddOptions<JokeGenerationOptions>()
    .Bind(builder.Configuration.GetSection(JokeGenerationOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JokeGenerationOptions>, JokeGenerationOptionsValidator>();

builder.Services.AddOptions<TopJokesOptions>()
    .Bind(builder.Configuration.GetSection(TopJokesOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TopJokesOptions>, TopJokesOptionsValidator>();

builder.Services.Configure<Dictionary<string, LlmProviderOptions>>(
    builder.Configuration.GetSection(LlmProviderOptions.SectionName));

builder.Services.AddScoped<IJokeRepository, JokeRepository>();
builder.Services.AddScoped<ITopJokeRepository, TopJokeRepository>();
builder.Services.AddScoped<ISchedulerLockRepository, SchedulerLockRepository>();
builder.Services.AddScoped<IJokeProfileRepository, JokeProfileRepository>();
builder.Services.AddScoped<IVoteRepository, VoteRepository>();

// Sign-in for voting: the cookie, its key ring in the database and the providers (see SignInSetup). A vote belongs to
// a voter key, a keyed hash of the account (see VoterKeys). Off while SignIn:VoterKeyPepper is empty.
builder.AddSignIn();

// "You might also like": Jev profiles, embeddings as the fallback (see JokeProfiler, JokeSimilarity).
builder.Services.AddOptions<SimilarityOptions>()
    .Bind(builder.Configuration.GetSection(SimilarityOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<SimilarityOptions>, SimilarityOptionsValidator>();
builder.Services.AddSingleton<SimilarityMetrics>();
builder.Services.AddHttpClient<IJevClient, JevClient>(http => http.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IJokeProfiler, JokeProfiler>();

builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
builder.Services.AddScoped<JokeGenerationService>();
builder.Services.AddScoped<TopJokeService>();
builder.Services.AddScoped<HtmlGeneratorService>();
builder.Services.AddSingleton<SchedulerStatus>();
builder.Services.AddSingleton<JokeReadCache>();
builder.Services.Configure<AppInfoOptions>(builder.Configuration.GetSection(AppInfoOptions.SectionName));
builder.Services.Configure<CloudflareOptions>(builder.Configuration.GetSection(CloudflareOptions.SectionName));
builder.Services.AddHostedService<JokeSchedulerService>();

var wwwrootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(wwwrootPath);

var app = builder.Build();
// Created now, so their series start at 0 before anything counts (see SimilarityMetrics).
app.Services.GetRequiredService<SimilarityMetrics>();
app.Services.GetRequiredService<SignInMetrics>();

// Pass an explicit PhysicalFileProvider so the middleware is not affected by
// the stale internal WebRootFileProvider (which is snapshotted before wwwroot exists).
var fileProvider = new PhysicalFileProvider(wwwrootPath);
app.UseForwardedHeaders();
app.UseMiddleware<CloudflareClientAddressMiddleware>();
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
// Before the rate limiter, so a limit can count per voter as well as per address.
app.UseAuthentication();
app.UseRateLimiter();
app.MapControllers();
// version: the image's commit (baked into the image as App__Version). revision: the Container Apps revision,
// unique per rollout even when re-deploying the same commit (the platform sets
// CONTAINER_APP_REVISION). Smoke tests wait for both, so they can't pass against a draining revision.
var appVersion = app.Services.GetRequiredService<IOptions<AppInfoOptions>>().Value.Version;
var appRevision = app.Configuration["CONTAINER_APP_REVISION"] ?? "local";
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", version = appVersion, revision = appRevision }));
// This process, new at every start: a restarted revision keeps its name, so the smoke tests' retry tells the processes
// apart by this.
var processId = Guid.NewGuid().ToString("N");
// What this process's scheduler did: its last tick per language, and the jokes it saved most recently (see SchedulerStatus).
// Only jokes that still exist: one can be deleted later as a duplicate copy, by this replica or another. If the database
// can't be reached, it reports what this process saved. signIn.keyRing: whether the sign-in cookies' key ring works here
// (see KeyRingCheck).
app.MapGet("/status", async (SchedulerStatus status, IJokeRepository jokes, KeyRingCheck keyRing, CancellationToken cancellationToken) =>
{
    var (ticks, savedJokes) = (status.LastTicks, status.SavedJokes);
    try
    {
        (ticks, savedJokes) = status.Existing((await jokes.GetExistingIdsAsync(status.ReportedJokeIds(), cancellationToken)).ToHashSet());
    }
    catch (Exception) when (!cancellationToken.IsCancellationRequested)
    {
    }
    return Results.Ok(new
    {
        version = appVersion,
        revision = appRevision,
        process = processId,
        ticks,
        savedJokes,
        signIn = new { keyRing = keyRing.State },
    });
});

// The page shell (wwwroot/index.html) depends only on the build and the configuration (the jokes
// are fetched by app.js), so write it once, before the server starts listening.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>().RegenerateAsync(CancellationToken.None);
}

// In the background: the database or Key Vault being slow must not hold up the start.
_ = app.Services.GetRequiredService<KeyRingCheck>().RunAsync(app.Lifetime.ApplicationStopping);

app.Run();
