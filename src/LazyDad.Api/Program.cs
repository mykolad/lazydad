var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// TODO Step 2: register LazyDadDbContext
// TODO Step 3: bind configuration options
// TODO Step 4: register LlmClientFactory and JokeGenerationService
// TODO Step 5: register JokeSchedulerService (IHostedService)

var app = builder.Build();

app.MapControllers();

// TODO Step 6: add /healthz endpoint

app.Run();
