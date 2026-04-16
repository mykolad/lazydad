# LazyDad — Claude Code Guide

## Project

LLM-powered dad joke generator. A background service calls Azure OpenAI on a
per-language schedule and persists jokes to Azure SQL. A static HTML page is
regenerated after every new joke. Deployed to Azure Container Apps.

## Solution layout

```
src/LazyDad.Api    — ASP.NET Core Web API (controllers, background services, configuration)
src/LazyDad.Data   — EF Core DbContext, entities, migrations, repositories
tests/LazyDad.Tests — xUnit + Moq unit tests
```

## Code style rules

- **No underscore prefix** on private fields (`context`, not `_context`).
- **`this.` only when required** to disambiguate a field from a same-named parameter.
- **No default arguments** on method parameters — callers always pass explicitly
  (e.g. `CancellationToken cancellationToken`, never `= default`).

## Key design decisions

- `JokeGenerationService` only generates text — the caller (`JokeSchedulerService`)
  is responsible for persisting and for triggering HTML regen. Keeps responsibilities
  small and scoped.
- `JokeSchedulerService` is a singleton `BackgroundService`; it uses
  `IServiceScopeFactory` to resolve scoped services (`JokeGenerationService`,
  `IJokeRepository`, `HtmlGeneratorService`) per operation.
- `HtmlGeneratorService.RegenerateAsync` rewrites `wwwroot/index.html` in full each
  time — simple and stateless.
- One `PeriodicTimer` loop per enabled language runs concurrently via `Task.WhenAll`.
- Distributed lock (`SchedulerLock` table) is scaffolded in the DB but not yet wired
  up — deferred until multi-replica becomes a concern.

## EF Core migrations

Always supply `--startup-project` so EF tools use DI and pick up user secrets:

```
dotnet ef migrations add <Name> --project src/LazyDad.Data --startup-project src/LazyDad.Api
dotnet ef database update          --project src/LazyDad.Data --startup-project src/LazyDad.Api
```

Do **not** use a design-time factory (`IDesignTimeDbContextFactory`) — it bypasses
user secrets and would require a hardcoded connection string.

## Local development

Credentials are stored in user secrets (never committed):

```
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<azure-sql-conn-string>" --project src/LazyDad.Api
dotnet user-secrets set "LlmProviders:AzureOpenAI:Endpoint"  "<endpoint>"              --project src/LazyDad.Api
dotnet user-secrets set "LlmProviders:AzureOpenAI:ApiKey"    "<key>"                   --project src/LazyDad.Api
```

Azure SQL firewall must allow the local machine's public IP.

## Azure resources

- **SQL server:** `lazydad-sql-swedencentral` (swedencentral)
- **OpenAI deployment:** `gpt-5.4-mini` on Azure OpenAI (swedencentral)
- **Container Apps:** Linux containers, min 2 replicas
- Port exposed by the container: **8080** (`ASPNETCORE_URLS=http://+:8080`)

## Building and testing

```
dotnet build lazydad.slnx
dotnet test  lazydad.slnx
```

## Docker

```
docker build -t lazydad .
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="..." \
  -e LlmProviders__AzureOpenAI__Endpoint="..." \
  -e LlmProviders__AzureOpenAI__ApiKey="..." \
  lazydad
```
