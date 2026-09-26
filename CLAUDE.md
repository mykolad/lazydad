# LazyDad — Claude Code Guide

## Project

LLM-powered dad joke generator. A background service calls Azure OpenAI on a
per-language schedule and persists jokes to Azure SQL. The page (a shell written at startup plus
`wwwroot/app.js` / `app.css`) loads the jokes, the Top 3 and the votes from the API.
Deployed to Azure Container Apps.

## Solution layout

```
src/LazyDad.Api    — ASP.NET Core Web API (controllers, background services, configuration)
src/LazyDad.Data   — EF Core DbContext, entities, migrations, repositories
tests/LazyDad.Tests      — xUnit + Moq unit tests (plus SQLite in-memory for repositories)
tests/LazyDad.SmokeTests — smoke tests against a deployed app (run by Deploy Master, not by Build and Test)
```

## Code style rules

- **No underscore prefix** on private fields (`context`, not `_context`).
- **`this.` only when required** to disambiguate a field from a same-named parameter.
- **No default arguments** on method parameters — callers always pass explicitly
  (e.g. `CancellationToken cancellationToken`, never `= default`).

## Key design decisions

- `JokeGenerationService` only generates text — the caller (`JokeSchedulerService`)
  is responsible for persisting and for updating the leaderboard. Keeps responsibilities
  small and scoped.
- `JokeSchedulerService` is a singleton `BackgroundService`; it uses
  `IServiceScopeFactory` to resolve scoped services (`JokeGenerationService`,
  `IJokeRepository`, `TopJokeService`) per operation. It records each language's next tick
  in `SchedulerStatus`, which the page's countdown reads via `/jokes/summary`.
- **The page** (design handoff "direction 2b"): `HtmlGeneratorService` writes `wwwroot/index.html`
  once, before the server listens. It holds only what depends on the build: the header, the version
  link, the loading skeleton, a pre-paint script that sets `data-theme` (no flash of the wrong theme),
  and a JSON config (language codes). `wwwroot/app.js` (plain JS, no build step) renders everything
  live: Top 3 spotlight (rotates every 7 s, paused on hover/focus or reduced motion), the "All jokes"
  feed (infinite scroll, pages of 20, sort Newest / Top voted), votes, copy/share, the countdown,
  UA/EN interface (jokes stay Ukrainian), and light/dark/system theme. Preferences and the reader's
  votes live in `localStorage`. `wwwroot/app.css` has the Organic design tokens (dark = reversed ramps).
  Brand files (sloth logo per theme, favicons, `site.webmanifest`) are static files in `wwwroot`.
  Design spec and deviations: `docs/design/redesign-2026-09.md`.
- **API for the page:** `GET /jokes/feed?sort=new|top&limit=(≤ 50)[&after=<next>]` → `{total, items, next}` (keyset cursor, so new jokes don't shift pages);
  `GET /jokes/summary` → `{count, nextBatchAt}`; `POST /jokes/{id}/vote {value, previous}` → `{up, down}`.
- **Votes are anonymous.** The browser remembers its vote and sends it as `previous`, so switching or
  removing adjusts the counts; the update is one atomic SQL `UPDATE` that never goes below zero. The
  endpoint is rate-limited to 30 votes per minute per client IP (from `X-Forwarded-For`, set by the
  Container Apps ingress). Server-side dedupe needs sign-in, which doesn't exist yet.
- One loop per enabled language (a delay to each due time, every `IntervalHours`) runs concurrently via `Task.WhenAll`.
  Within a tick, all of a language's `LlmModels` are called in parallel, each in its
  own DI scope (a `DbContext` must not be shared across concurrent calls); jokes are
  then persisted sequentially.
- **Top-N leaderboard** (`TopJokes` config, `TopJokeService`, `TopJokes` table): after
  each tick a reasoning "judge" model (`TopJokes:Judge`, e.g. `gpt-6-sol`) sees the
  current top N plus the new jokes and returns the new ranking as a JSON-schema
  structured response. If the leaderboard is empty or short, it is seeded from the last
  `SeedSampleSize` jokes. Invalid verdicts (unknown/duplicate ids, wrong count) are
  discarded; an unchanged ranking skips the DB write. `ReplaceAsync` does
  delete + insert in one transaction.
- Russian language support was removed (migration `RemoveRussianJokes` purges its rows).
- Distributed lock (`SchedulerLock` table) is scaffolded in the DB but not yet wired
  up. That's safe only because the app runs a single replica; it must be wired up
  before scaling out (issue #5).

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

- **SQL server:** `lazydad-sql-swedencentral` (swedencentral).
  - Prod: `lazydad-db`, **Basic** DTU tier (5 DTU, 2 GB), always on. Serverless was dropped:
    every 4-hour tick woke it for the 60-minute auto-pause minimum, which cost about $74/month.
  - Staging: `lazydad-db-staging`, serverless on the **free offer** (100k vCore-s/month). It
    pauses when idle; if the free amount runs out it stays paused until next month, and staging
    deploys fail until then.
  - The app raises the SQL connect timeout to 60 s (`SqlConnectionStrings.WithResumeTimeout`), so a login waits
    for a paused database to resume instead of timing out after the default 15 s.
- **Migrations** run in Deploy Master as an EF migration bundle, against staging and then prod (see below).
  The app never migrates on startup.
- **Azure OpenAI** (swedencentral): each `LlmModels[].Model` in config is the Azure deployment name (e.g. `gpt-5.3-chat`)
- **Container Apps** (environment `lazydad-cae`, Consumption, 0.5 vCPU / 1 GiB):
  - `lazydad-app` (prod): **exactly one replica** (min = max = 1), no health probes yet.
    Scaling out needs the scheduler lock first (see issue #6); the vote rate limit is per replica.
  - `lazydad-app-staging`: 0–1 replicas (scales to zero when idle). Calls the real LLMs.
    **Ingress allows listed IPs only** (the owner's `home` rule; Deploy Master adds its runner temporarily),
    so stray visitors can't wake it and spend LLM tokens.
  - Both pull from ACR with the `lazydad-acr-pull` managed identity; the ACR admin user is disabled.
- Port exposed by the container: **8080** (`ASPNETCORE_URLS=http://+:8080`)
- **Version metadata is baked into the image.** Build Image (`build-image.yml`) passes build args, and the Dockerfile turns
  them into `App__Version` (short SHA), `App__Revision` (full SHA), `App__CommitDate`, `App__SourceUrl`
  (`AppInfoOptions`) and the standard OCI labels. The page shows **CalVer + SHA** under the Top 3,
  e.g. `v2026.09.25 e33d99a`, linked to the commit (`vdev (local build)` otherwise).
  Deploys remove any `App__Version` container setting, so the image is the only source.
- `/healthz` returns `{status, version, revision}`: `version` is the image commit (short SHA),
  `revision` is the platform's `CONTAINER_APP_REVISION`, unique per rollout. Smoke tests wait for both.
- `/status` returns the version, revision and this process's last scheduler tick per language
  (succeeded, saved joke ids and models, leaderboard outcome, and the error type only, no details).
- **Setup runbook:** `infra/deployment-setup.md` has the one-time Azure/GitHub setup behind Deploy Master,
  including the weekly registry purge task (`purge-old-images`: keeps the last 30 days, 10 older
  images, and what each environment runs: revisions are pinned to the image digest, and the manifest
  stays tagged `deployed-<env>` / `deploying-<env>`, applied in two phases around each rollout; `previous-<env>`
  marks what served before the latest rollout, for the automatic rollback).

## Building and testing

```
dotnet build lazydad.slnx
dotnet test  lazydad.slnx
```

## Build and Test

`.github/workflows/build-and-test.yml` (**Build and Test**, job `build-and-test`, the required check on `master`)
runs on every PR and on pushes to `master`: build, then
`tools/coverage.ps1` for tests, coverage (coverlet → ReportGenerator, pinned in `dotnet-tools.json`),
and the gate. The job fails if line coverage is below the script's `$MinLineCoverage`.
Coverage settings (included assemblies, migrations excluded) live in
`tests/LazyDad.Tests/coverage.runsettings`. The workflow runs the same script, so a local run reproduces the gate:

```
./tools/coverage.ps1        # HTML report at coverage/index.html
```

Raise `$MinLineCoverage` as coverage grows; never lower it to get a PR through.
The script runs only `tests/LazyDad.Tests` (the smoke tests need a deployed app; Deploy Master runs them).
Build and Test still compiles the smoke project, because its build step builds the whole solution.

Its second job, **`clean-database-migrations`**, runs against a throwaway SQL Server 2022 service container
(SQL auth; the password is not a secret). It checks the database side that neither the SQLite unit tests
(`EnsureCreated()`) nor staging (incremental upgrades only) exercise:
1. `dotnet ef migrations has-pending-model-changes`: a model change without a migration fails the PR.
2. The deploys' migration bundle applies the **whole chain to an empty database**, rolls every migration back
   (`efbundle 0`), and applies them again. `RemoveRussianJokes.Down` is a deliberate no-op.
3. The repository tests (`*RepositoryTests`) run against SQL Server: with `SQLSERVER_TEST_CONNECTION` set,
   `TestDatabase` gives each test class its own database built by the migrations (SQLite otherwise).

## Deploy Master

`.github/workflows/deploy-master.yml` (**Deploy Master**) runs after Build and Test succeeds on a push to `master` (or manually via
*Run workflow*). It builds once and promotes the same image:

1. **build** (the reusable `.github/workflows/build-image.yml`, **Build Image**) builds the image
   `lazydad:<short-sha>`, pushes it to ACR (outputting its digest), and builds the EF migration bundle
   (`dotnet-ef`, pinned in `dotnet-tools.json`).
2. **staging** then **production**: the same reusable `.github/workflows/deploy-environment.yml` (**Deploy Environment**) in each
   environment. It opens the SQL firewall for the runner, runs the bundle, closes the firewall,
   protects the running and the new image with tags, rolls the app to the image **by digest**
   (its version metadata is baked in; any old `App__Version` setting is removed), moves
   `deployed-<environment>` to it,
   allows the runner through staging's IP
   restrictions, and runs `tests/LazyDad.SmokeTests`
   against it. If they fail, it restarts the new revision (a fresh startup tick) and runs them once more,
   counting only ticks completed after the restart (`SMOKE_TICKS_AFTER`).
3. **roll-back**, only if production failed **after its new revision took traffic**: the reusable
   `.github/workflows/roll-back.yml` (**Roll Back**) puts back the image that served before, which Deploy
   Environment tags `previous-<environment>` before each rollout. It does nothing if production never
   switched to the new revision, or if the same image served before. It doesn't roll back migrations, and
   the run still ends as failed, so GitHub notifies you.

Promotion is automatic: production runs only if staging's smoke tests pass. Azure login is
OIDC through the `lazydad-github-cd` managed identity, trusted via GitHub's immutable subjects
(`repo:mykolad@<id>/lazydad@<id>:environment:<env>`); nothing secret lives in GitHub except
each environment's `SQL_CONNECTION_STRING`. Both environments only accept deployments from
`master`.

The smoke tests check that `/healthz` reports the new version and revision, that the page and API are served,
that the new revision itself saved a joke from every model configured in `appsettings.json` and ran
the judge (it reports its own last tick on `/status`; DB rows alone could come from the draining
revision), that those jokes are in `/jokes`, that
the leaderboard is populated with valid ranks, that `app.js`/`app.css`, `/jokes/feed` and `/jokes/summary`
are served, and that the vote endpoint answers (with a no-op vote, so it never changes the counts).
To run them against staging locally:

```
$env:SMOKE_BASE_URL = "https://lazydad-app-staging.<env-domain>.westeurope.azurecontainerapps.io"
# optional: $env:SMOKE_EXPECTED_VERSION, $env:SMOKE_EXPECTED_REVISION
dotnet test tests/LazyDad.SmokeTests
```

### Deploy Branch to Staging (preview a branch before merging)

`.github/workflows/deploy-branch-to-staging.yml` (**Deploy Branch to Staging**) is manual: on GitHub,
open **Actions → Deploy Branch to Staging → Run workflow** and pick the branch. It runs the same
Build Image and Deploy Environment steps, smoke tests included, against **staging only**.
- **Production can't be reached from it:** the `production` environment accepts `master` only.
  `staging` also accepts `*/*` branches (`feature/…`, `fix/…`).
- **Migrations are off by default** (the *run-migrations* checkbox). Staging keeps any migration it
  applies, so only tick it for a branch whose migrations you'll merge unchanged.
- **It shares the `deploy` concurrency group with Deploy Master,** so the two never interleave on
  staging. The next Deploy Master run puts staging back on `master`.

### Roll Back Production

`.github/workflows/roll-back-production.yml` (**Roll Back Production**) is manual too: **Actions → Roll Back
Production → Run workflow** on `master`. It puts production back on an earlier master build without rebuilding.
- **Which version:** the *version* input (the short SHA shown on the page). Left empty, it's the image of the
  most recent earlier revision that ran a different image, i.e. "undo the last deploy".
- **What it runs:** the reusable Roll Back workflow (shared with Deploy Master's automatic rollback). Its
  resolve job finds the image's digest and reads the commit from the image's label.
  Then production runs the same Deploy Environment steps (protection tags, rollout by digest, that commit's
  smoke tests), **without migrations**. The database keeps its current schema, so the older code must work
  with it (additive migrations do).
- **What it refuses:** images the purge has deleted, images built before the version was baked in (#17),
  commits that aren't on master, and the image production already runs.
- **The next Deploy Master run rolls forward again.** To stay on the old version, revert on master.
- Its resolve job logs in through the `production` environment, so each rollback shows an extra
  production deployment in GitHub.

`tsg/redeploy.ps1` is only a manual fallback now. It skips staging, migrations and smoke tests.

## Docker

```
docker build -t lazydad .
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="..." \
  -e LlmProviders__AzureOpenAI__Endpoint="..." \
  -e LlmProviders__AzureOpenAI__ApiKey="..." \
  lazydad
```
