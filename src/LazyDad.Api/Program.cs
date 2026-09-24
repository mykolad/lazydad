using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<LazyDadDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
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

builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
builder.Services.AddScoped<JokeGenerationService>();
builder.Services.AddScoped<TopJokeService>();
builder.Services.AddScoped<HtmlGeneratorService>();
builder.Services.AddHostedService<JokeSchedulerService>();

var wwwrootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(wwwrootPath);

var app = builder.Build();

// Pass an explicit PhysicalFileProvider so the middleware is not affected by
// the stale internal WebRootFileProvider (which is snapshotted before wwwroot exists).
var fileProvider = new PhysicalFileProvider(wwwrootPath);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
app.MapControllers();
// CD sets App__Version to the image's commit, so smoke tests can tell the new revision is serving.
var appVersion = app.Configuration["App:Version"] ?? "dev";
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", version = appVersion }));

// Regenerate the HTML page from existing jokes once the server is listening, off the startup
// path: a slow or unreachable DB (including EF's retry delays) must not keep /healthz down.
// A failure is only logged; the scheduler's first tick regenerates the page again.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var htmlGenerator = scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>();
        await htmlGenerator.RegenerateAsync(app.Lifetime.ApplicationStopping);
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Startup HTML regeneration failed; the scheduler will regenerate the page on its next tick.");
    }
}));

app.Run();
