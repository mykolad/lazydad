using LazyDad.Api.Services;
using LazyDad.Data;
using LazyDad.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LazyDad.Tests;

/// <summary>
/// The whole app, as Program.cs builds it, in memory: what's between HTTP and the controllers (routing, binding, the
/// middleware order, the rate limiter) is otherwise only checked by the smoke tests, after a deploy. Its database is a
/// <see cref="TestDatabase"/>, the scheduler doesn't run (no LLM calls), and the shell is written to a temporary
/// content root. The environment isn't Development, which would load the user secrets: production's database.
/// </summary>
public sealed class LazyDadApp : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> settings;
    private readonly string contentRoot = Directory.CreateTempSubdirectory("lazydad-app-").FullName;

    public LazyDadApp(Dictionary<string, string?> settings)
    {
        this.settings = settings;
        // The app reads its settings and serves its static files from its content root: the real appsettings.json and
        // wwwroot, which the build copies here.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), Path.Combine(contentRoot, "appsettings.json"));
        var wwwroot = Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot")).FullName;
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
            File.Copy(file, Path.Combine(wwwroot, Path.GetFileName(file)));
    }

    public TestDatabase Database { get; } = new();

    public async Task<Joke> AddJokeAsync(string text)
    {
        var joke = new Joke { Language = "Ukrainian", Model = "gpt-6-luna", Text = text, GeneratedAt = DateTime.UtcNow };
        await using var context = Database.CreateContext();
        context.Jokes.Add(joke);
        await context.SaveChangesAsync();
        return joke;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(contentRoot);
        // Settings, not app configuration: Program.cs reads some of them (the vote limit) while it registers services.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<LazyDadDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<LazyDadDbContext>>();
            services.AddDbContext<LazyDadDbContext>(Database.Configure);
            services.Remove(services.Single(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(JokeSchedulerService)));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // The factory can call this more than once (Dispose after DisposeAsync).
        if (!disposing || !Directory.Exists(contentRoot))
            return;
        Database.Dispose();
        Directory.Delete(contentRoot, recursive: true);
    }
}
