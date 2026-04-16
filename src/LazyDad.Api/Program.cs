using LazyDad.Api.Configuration;
using LazyDad.Api.Services;
using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<LazyDadDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<JokeGenerationOptions>(
    builder.Configuration.GetSection(JokeGenerationOptions.SectionName));

builder.Services.Configure<Dictionary<string, LlmProviderOptions>>(
    builder.Configuration.GetSection(LlmProviderOptions.SectionName));

builder.Services.AddScoped<IJokeRepository, JokeRepository>();

builder.Services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
builder.Services.AddScoped<JokeGenerationService>();
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
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

// Regenerate the HTML page from existing jokes on every startup.
using (var scope = app.Services.CreateScope())
{
    var htmlGenerator = scope.ServiceProvider.GetRequiredService<HtmlGeneratorService>();
    await htmlGenerator.RegenerateAsync(CancellationToken.None);
}

app.Run();
