using System.Threading.RateLimiting;
using LazyDad.Api.Configuration;
using LazyDad.Api.Controllers;
using LazyDad.Api.Services;
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

// Container Apps' ingress is the only way in; it appends the caller's address to X-Forwarded-For.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
// Votes are anonymous, so at least cap how fast one address can cast them.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(JokesController.VotePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

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

builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
builder.Services.AddScoped<JokeGenerationService>();
builder.Services.AddScoped<TopJokeService>();
builder.Services.AddScoped<HtmlGeneratorService>();
builder.Services.AddSingleton<SchedulerStatus>();
builder.Services.Configure<AppInfoOptions>(builder.Configuration.GetSection(AppInfoOptions.SectionName));
builder.Services.AddHostedService<JokeSchedulerService>();

var wwwrootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(wwwrootPath);

var app = builder.Build();

// Pass an explicit PhysicalFileProvider so the middleware is not affected by
// the stale internal WebRootFileProvider (which is snapshotted before wwwroot exists).
var fileProvider = new PhysicalFileProvider(wwwrootPath);
app.UseForwardedHeaders();
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
app.UseRateLimiter();
app.MapControllers();
// version: the image's commit (baked into the image as App__Version). revision: the Container Apps revision,
// unique per rollout even when re-deploying the same commit (the platform sets
// CONTAINER_APP_REVISION). Smoke tests wait for both, so they can't pass against a draining revision.
var appVersion = app.Services.GetRequiredService<IOptions<AppInfoOptions>>().Value.Version;
var appRevision = app.Configuration["CONTAINER_APP_REVISION"] ?? "local";
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", version = appVersion, revision = appRevision }));
// What this process's scheduler did on its last tick per language (see SchedulerStatus).
app.MapGet("/status", (SchedulerStatus status) => Results.Ok(new { version = appVersion, revision = appRevision, ticks = status.LastTicks }));

// The page shell (wwwroot/index.html) depends only on the build and the configuration (the jokes
// are fetched by app.js), so write it once, before the server starts listening.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>().RegenerateAsync(CancellationToken.None);
}

app.Run();
