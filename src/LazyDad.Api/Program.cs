using LazyDad.Api.Configuration;
using LazyDad.Data;
using LazyDad.Data.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<LazyDadDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<JokeGenerationOptions>(
    builder.Configuration.GetSection(JokeGenerationOptions.SectionName));

builder.Services.Configure<Dictionary<string, LlmProviderOptions>>(
    builder.Configuration.GetSection(LlmProviderOptions.SectionName));

builder.Services.AddScoped<IJokeRepository, JokeRepository>();

// TODO Step 4: register LlmClientFactory and JokeGenerationService
// TODO Step 5: register JokeSchedulerService (IHostedService)

var app = builder.Build();

app.MapControllers();

// TODO Step 6: add /healthz endpoint

app.Run();
