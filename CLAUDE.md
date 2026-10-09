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
tests/load               — the load test: k6 visitors (visitors.js) and the database seeding (Seed.cs, a file-based app)
```

## Code style rules

- **No underscore prefix** on private fields (`context`, not `_context`).
- **`this.` only when required** to disambiguate a field from a same-named parameter.
- **No default arguments** on method parameters — callers always pass explicitly
  (e.g. `CancellationToken cancellationToken`, never `= default`).
- **Tests check behaviour, not logs.** Assert on outcomes: what's saved, returned or sent, `/status`, metrics, spans.
  Never on log messages (their wording, level or content), and don't add tests whose only purpose is a log line:
  logging is for diagnosing, and changing a message must not break a test. To wait for background work, wait for its
  observable result (e.g. `SchedulerStatus`), not for a log line.

## Key design decisions

- `JokeGenerationService` only generates text — the caller (`JokeSchedulerService`)
  is responsible for persisting and for updating the leaderboard. Keeps responsibilities
  small and scoped. The model answers with the joke, a `---` line, and one English sentence on why it's funny
  (`JokeDraft`; `Jokes.Explanation`, nullable, 500 characters): no extra call. An answer without the separator is all
  joke, with no explanation. The page hides the explanation behind a lightbulb on each feed row and on a joke's page.
- `JokeSchedulerService` is a singleton `BackgroundService`; it uses
  `IServiceScopeFactory` to resolve scoped services (`JokeGenerationService`,
  `IJokeRepository`, `TopJokeService`) per operation. It records each language's next tick
  in `SchedulerStatus`, which the page's countdown reads via `/jokes/summary`.
- **The page** (design handoff "direction 2b"): `HtmlGeneratorService` writes `wwwroot/index.html`
  once, before the server listens. It holds only what depends on the build: the header, the version
  link, the loading skeleton, a pre-paint script that sets `data-theme` (no flash of the wrong theme),
  and a JSON config (language codes). Its asset URLs are absolute, since the same shell also serves `/j/<id>`.
  `wwwroot/app.js` (plain JS, no build step) renders everything
  live: Top 3 spotlight (rotates every 7 s, paused on hover/focus or reduced motion), the "All jokes"
  feed (infinite scroll, pages of 20, sort Newest / Top voted), votes, link sharing, the lightbulbs ("why it's funny"),
  the countdown, UA/EN interface (jokes stay Ukrainian), and light/dark/system theme. Preferences and the reader's
  votes live in `localStorage`. `wwwroot/app.css` has the Organic design tokens (dark = reversed ramps).
  Brand files (sloth logo per theme, favicons, `site.webmanifest`) are static files in `wwwroot`.
  Design spec and deviations: `docs/design/redesign-2026-09.md`.
- **A joke's page** (`/j/<id>`, what every share button shares): `JokePageController` serves the same shell per request,
  with the joke in `<title>` and the link-preview tags (Open Graph, `canonical`) that messengers read without running
  `app.js` (404 and the plain shell for an unknown joke). `app.js` routes with the History API: a joke opened from the
  list is shown in place, and Back or "All jokes" returns to the list's scroll position. Below the joke, "You might also
  like" (next point).
- **Similar jokes** (`/jokes/{id}/similar`): ranked from stored profiles (`JokeProfiles` table, one row per joke and
  kind, with the version that made it), never by a call at request time. `JokeProfiler` asks for each joke's profile
  once, after its tick (the tick's own jokes, then older ones without one, up to `Similarity:BatchSize`, 200, a tick,
  so the backfill is gradual), one replica at a time (its own `profiles` lease in `SchedulerLocks`, 30 minutes, released when the batch is done;
  no new call starts in its last 5 minutes): **Jev** (`JevClient`, jevtypesafeai.com, the pinned `Similarity:Jev:Model`; the topic and the kind of
  wordplay as probabilities, the questions in `JevQuestions`) and an **embedding** (`text-embedding-3-small`, 512
  dimensions, through `LlmClientFactory`). `JokeSimilarity` ranks by Jev (cosine, wordplay at half weight), falls back
  to embeddings for a joke without a Jev profile, and to shared words without either. Jev won the blind test in
  `experiments/similarity` (#63): its suggestions share the joke's style rather than its subject. Only the joke and its
  explanation go to Jev. The key is a Key Vault reference per app (`JevApiKey`, `JevApiKeyStaging`; runbook section 7,
  step 5); without one, Jev is off. A failed profile never fails the tick: the next one retries. `SimilarityMetrics`
  counts Jev requests (and the cost and credits Jev reports), profiles, and which method ranked each request.
  Changing a Jev question or option means a new `JevQuestions.QuestionSet` (every joke is profiled again).
- **API for the page:** `GET /jokes/feed?sort=new|top&limit=(≤ 50)[&after=<next>]` → `{total, items, next}` (keyset cursor, so new jokes don't shift pages);
  `GET /jokes/summary` → `{count, nextBatchAt}`; `POST /jokes/{id}/vote {value, previous}` → `{up, down}`;
  `GET /jokes/{id}` (cached like the feed); `GET /jokes/{id}/similar?limit=(≤ 12)` → the most similar jokes;
  `GET /me` → `{signedIn, provider}`, `GET /auth/signin/{provider}?returnUrl=`, `POST /auth/signout` (never cached; sign-out
  only with the page's `X-LazyDad: 1` header, which another site's form can't send).
- **Read cache** (`JokeReadCache`, `ReadCache:Seconds`, 30 by default, 0 = off): the joke count, the Top 3, each joke by id, the
  similarity index and each feed page (by sort, cursor and size) are kept in memory per replica (at most 20,000 rows), since every visitor reads
  the same ones and the load test found the database to be the first limit (`docs/performance.md`, the history of
  load tests and optimizations). Votes aren't cached; other visitors see a vote's counts when their copy expires. The
  scheduler drops its replica's copies when it saves a joke or updates the Top 3; the other region's replica catches up
  within the lifetime.
- **Votes are anonymous.** The browser remembers its vote and sends it as `previous`, so switching or
  removing adjusts the counts; the update is one atomic SQL `UPDATE` that never goes below zero. The
  endpoint is rate-limited to 30 votes per minute per client IP (`RateLimiting:VotesPerMinute`, `VoteRateLimit`; only the
  load test raises it): from `X-Forwarded-For` (set by the Container Apps
  ingress), or, for requests from Cloudflare's ranges, from `CF-Connecting-IP` (`CloudflareClientAddressMiddleware`;
  anyone can send that header, so only Cloudflare's count). Server-side dedupe needs sign-in, which doesn't exist yet.
- **Sign-in for voting is in progress** (issues #68–#83, one PR each, in the design's order; design and decisions in
  `docs/design/sign-in-2026-10.md`). Landed so far, unused by the page:
  - the `Votes` table (`(JokeId, VoterKey)`, `Value`, `UpdatedAt`) and `VoteRepository`, which changes a vote row only if
    it still holds the vote it read and the joke's `Up`/`Down` by the difference, in one transaction (idempotent; a lost
    race reads again);
  - `VoterKeys`: HMAC-SHA256 of `<provider>:<account id>` keyed with `SignIn:VoterKeyPepper` (a Key Vault reference per
    app, `VoterKeyPepper` / `VoterKeyPepperStaging`; runbook section 7, step 7). Empty = sign-in off. Never store or log
    the account id, a claim or a voter key;
  - the cookie (`SignInSetup`): `__Host-lazydad`, HttpOnly, Secure, `SameSite=Lax`, a session cookie for now, holding
    exactly the voter key and the provider (`SignInPrincipal`; any other cookie is rejected). With a Key Vault key
    (`DataProtection:KeyVaultKeyId`, versionless; required wherever sign-in is on, except Development) its key ring is in
    the database (`DataProtectionKeys`, application name `lazydad`, so both production apps share it; always the
    read-write connection), each key wrapped with that key. Without one (a local run) the ring stays on the machine, so a
    local run against production's database never adds an unencrypted key to production's ring. `KeyRingCheck` proves the ring at startup in the background (`/status`
    `signIn.keyRing`: `off`, `pending`, `ok`, `failed`);
  - `SignInController`: `GET /auth/signin/{provider}?returnUrl=` (local URLs only; 404 for a provider that isn't
    enabled), `POST /auth/signout`, `GET /me` → `{signedIn, provider}`, all `no-store`. Providers are authentication
    schemes named after them; their callbacks go through `SignInEvents` (only the voter key survives;
    `lazydad_signins_total{provider, outcome}`, `completed` counted once the cookie is written). `EnabledSignInProviders` lists the registered provider schemes, so
    the controller has one path for all. A provider's scheme exists only while sign-in is on and the provider is
    configured;
  - **GitHub** (`AspNet.Security.OAuth.GitHub`, `/signin-github`): no scopes, PKCE, the account id is the numeric `id`
    (never the login, which can change). `SignIn:GitHub:ClientId` and `ClientSecret` (a Key Vault reference per app,
    `GitHubClientSecret` / `GitHubClientSecretStaging`; one OAuth app per environment; runbook section 7, step 8): both
    or neither, empty = off. Signing in works by hand at `/auth/signin/github`; the page has no button yet (#72);
  - `dev` (`DevelopmentSignInHandler`, in Development only: signs in at once; `?account=` picks the made-up account, to
    vote as several readers).
- One loop per enabled language runs concurrently via `Task.WhenAll`: a startup tick, then a delay to each regular
  due time. **Due times are fixed UTC times** (`TickSchedule`: every whole `IntervalHours` since midnight UTC, so
  00:00, 04:00, 08:00 … for 4 h), the same for every replica and unchanged by restarts. After a startup tick the
  first regular one is at least half a period away, so a restart just before a due time doesn't add a second batch
  minutes later.
  Within a tick, all of a language's `LlmModels` are called in parallel, each in its
  own DI scope (a `DbContext` must not be shared across concurrent calls); each joke is
  saved as soon as its model answers (in its own scope too), so a slow model delays only its own joke. `/status`
  lists each saved joke right away; the judge runs once every model is done. A model that errors or answers empty is
  asked again, up to `JokeGeneration:Attempts` (3) tries `RetryDelaySeconds` (30) apart; only the final outcome is
  counted (`lazydad_jokes_total`), so a joke saved on a later try isn't a failure.
  **No joke twice:** a joke the site already has (`JokeText.Key`: the same letters and digits, ignoring case, punctuation,
  quotes and apostrophes) isn't saved; the model is asked again at once, told which joke it repeated, within the same
  tries (outcome `duplicate` if every try repeats one). The prompt's recent jokes are only the latest 20, and the old
  model saved 53 copies weeks apart. Each joke stores `TextHash` (SHA-256 of its key, indexed with the language), so the
  check is one index lookup however many jokes there are; a tick first fills any missing hashes. After saving, before
  the judge sees the new jokes, `RemoveDuplicatesAsync` deletes later copies of the last day's jokes (through the date
  and hash indexes; a full GROUP BY over the hash index on each process's first cleanup and then daily), keeping
  the Top 3 copy, else the most-voted, else the oldest, never a Top 3 joke, with their profiles and votes; one replica
  at a time, under a `duplicates:<language>` lease. The judge also gets one candidate per joke text.
- **Top-N leaderboard** (`TopJokes` config, `TopJokeService`, `TopJokes` table): after
  each tick a reasoning "judge" model (`TopJokes:Judge`, e.g. `gpt-6-sol`) sees the
  current top N plus the new jokes and returns the new ranking as a JSON-schema
  structured response. If the leaderboard is empty or short, it is seeded from the last
  `SeedSampleSize` jokes. Invalid verdicts (unknown/duplicate ids, wrong count) are
  discarded; an unchanged ranking skips the DB write. `ReplaceAsync` does
  delete + insert in one transaction.
- Russian language support was removed (migration `RemoveRussianJokes` purges its rows).
- **One batch per language per period across replicas** (`SchedulerLocks` table, `SchedulerLockRepository`):
  a tick takes a lease `jokes:<language>` that lasts until just before the next due time (that due time minus
  min(5 min, period/10)); every replica computes the same due times, so their ticks until then skip. A replica whose timer fires while another holds it skips (`/status` leaderboard
  `skipped`); if the holder dies, the next replica whose timer fires takes over. Taking an expired lease is one
  atomic `UPDATE`, and the first insert is guarded by the primary key. The **startup tick always runs** and takes
  the lease: a new revision proves itself with it (smoke tests), while the old revision usually still holds it.
  So a replica start (deploy, restart, scale-out) still means one extra batch, but not a new rhythm: its lease
  lasts until the first regular due time after it, which it or any replica then runs.
- **Telemetry** (`src/LazyDad.Api/Telemetry`): OpenTelemetry traces, metrics and logs over OTLP to Grafana Cloud
  (EU), on only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (the Container Apps settings; runbook section 11). The standard
  `OTEL_*` variables apply, with `http/protobuf` as the default protocol. The resource names the app (`service.name`) and
  the replica (`service.instance.id`, and `grafana.host.id`, which Grafana's Application Observability counts hosts
  by: on the free tier, 2,232 host-hours a month, about three always-on replicas). A trace per request (not `/healthz`) and per
  scheduler tick (`joke tick`); `SchedulerMetrics` counts ticks, jokes and leaderboard updates by outcome, next to
  `/status`. **No visitor data:** `PersonalDataFilter` strips IPs and user agents from spans before export (a test
  checks the exported bytes), and the LLM spans don't record prompts or responses. All `ILogger` logs are exported,
  including the model output they contain on purpose (saved jokes, an invalid judge answer): never log anything about
  visitors. Keep metric tags bounded (no joke ids). EF Core's per-command log (`Executed DbCommand` with the SQL) is off
  outside Development (`Microsoft.EntityFrameworkCore.Database.Command` at `Warning`: failed commands still log); SQL
  shows up in the traces and the dashboard's SQL panel instead. A failed LLM call logs the provider's error body.
- `LlmClientFactory` keeps **one chat client per model** for the app's lifetime (wrapped with `UseOpenTelemetry()`);
  callers may still dispose theirs (a no-op). Creating one per call would restart the LLM metrics every tick.

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
# Entra ID with your az login (you are the server's Entra admin); no password:
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=tcp:lazydad-sql-swedencentral.database.windows.net,1433;Database=lazydad-db;Authentication=Active Directory Default;Encrypt=True" --project src/LazyDad.Api
dotnet user-secrets set "LlmProviders:AzureOpenAI:Endpoint"  "<endpoint>"              --project src/LazyDad.Api
# No API key: key authentication is off; the app uses your `az login` (you have "Foundry User" on lazydad-openai-resource).
```

Azure SQL firewall must allow the local machine's public IP.

To try sign-in locally, give it a pepper of its own (any 32 random bytes; never production's). Leave
`DataProtection:KeyVaultKeyId` unset: the key ring then stays on your machine, out of the database. The `dev` provider
signs you in at `/auth/signin/dev?account=alice`:

```
dotnet user-secrets set "SignIn:VoterKeyPepper" "$([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))" --project src/LazyDad.Api
```

Telemetry is off locally. To see traces, metrics and logs while developing, run the .NET Aspire dashboard and point the
app at it (don't point a local run at Grafana: it would mix with production's data):

```
docker run --rm -p 18888:18888 -p 4318:18890 -e DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true mcr.microsoft.com/dotnet/aspire-dashboard
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4318"   # then dotnet run; the UI is http://localhost:18888
```

## Azure resources

- **SQL server:** `lazydad-sql-swedencentral` (swedencentral).
  - Prod: `lazydad-db`, **Basic** DTU tier (5 DTU, 2 GB), always on. Serverless was dropped:
    every 4-hour tick woke it for the 60-minute auto-pause minimum, which cost about $74/month.
    Backups: point-in-time for 7 days, long-term weekly for 7 weeks and monthly for 12 months, geo-redundant storage,
    and a `CanNotDelete` lock on the database (runbook section 4; restoring: section 12). The app and the migrations
    use the name `lazydad-db`, so a restore swaps names rather than repointing anything.
  - Staging: `lazydad-db-staging`, serverless on the **free offer** (100k vCore-s/month). It
    pauses when idle; if the free amount runs out it stays paused until next month, and staging
    deploys fail until then.
  - The app raises the SQL connect timeout to 60 s (`SqlConnectionStrings.WithResumeTimeout`), so a login waits
    for a paused database to resume instead of timing out after the default 15 s.
  - **Entra-only authentication** (no SQL logins at all; runbook sections 4 and 8). Database
    users: `lazydad-production` (read/write) and `lazydad-github-cd` (ddladmin, read/write) in `lazydad-db`;
    `lazydad-app-staging` and `lazydad-github-staging` likewise in `lazydad-db-staging`. Both prod apps connect as one
    user-assigned identity, `lazydad-production` (`Authentication=Active Directory Managed Identity;User Id=<its client
    id>`, and `AZURE_CLIENT_ID` for `DefaultAzureCredential`); staging as its system-assigned identity; migrations as
    the environment's deploy identity; you as the server's Entra admin.
- **Migrations** run in Deploy Master as an EF migration bundle, against staging and then prod (see below).
  The app never migrates on startup.
- **Azure OpenAI** (`lazydad-openai-resource`, AI Services, eastus2): each `LlmModels[].Model` in config is the deployment name. Non-OpenAI models
  deployed there (e.g. `Kimi-K2.5`, a thinking model: 10–60 s and ~3–4k output tokens per joke) are called through the same Azure OpenAI chat API.
  **Key authentication is off** (`disableLocalAuth`; runbook section 5): the apps call it as their
  managed identities (`lazydad-production`, staging's own) with the "Foundry User" role, so
  `LlmProviders:AzureOpenAI:ApiKey` is empty everywhere.
  `ROLLBACK_MIN_COMMIT` (a repository variable, #30's merge commit) keeps Roll Back from choosing older images, which
  only knew the key.
- **Container Apps** (Consumption, 0.25 vCPU / 0.5 GiB each, the smallest size), production in **two regions**:
  - `lazydad-app` (prod, environment `lazydad-cae`, West Europe) and `lazydad-app-swedencentral` (prod, environment
    `lazydad-cae-swedencentral`, Sweden Central, next to the database): **exactly one replica each** (min = max = 1),
    with startup, liveness and readiness probes on `/healthz` (no database check: both regions share the database;
    runbook section 7, step 6), like staging. Same image, same settings, same database; the scheduler lease makes one of them run each batch
    (each app's startup tick still runs). The vote rate limit is per replica.
    Public address: **`lazydad.fyi`**, through Cloudflare's proxy (Free plan: DDoS protection, bot settings, a rate-limit
    rule on votes) to **Azure Traffic Manager** (`lazydad-traffic`, weighted 1:1, HTTPS health checks on each app's
    `/healthz`), which leaves out an app that stops answering (runbook section 10). The Cloudflare Origin CA certificate
    is in Key Vault (`lazydad-fyi-origin`), and both environments read it as `lazydad-production`.
    The apps' ingress admits only Cloudflare's IPv4 ranges and Traffic Manager's probe addresses (the
    `AzureTrafficManager` service tag, about 210): without those, the health checks would get `403` whether the app runs
    or not. Cloudflare's ranges also live in `appsettings.json` (`Cloudflare:IpRanges`).
    The switch is the `production` environment's `CLOUDFLARE_ONLY_INGRESS` variable: while it's `true`, every deploy
    creates the `cloudflare-*` and `trafficmanager-*` ingress rules if missing and keeps them in sync with both lists
    (`tools/cloudflare-ranges.sh`: one PATCH of the whole rule list, at most 3 Cloudflare / 50 Traffic Manager removals
    at once; skipped if either list can't be read; the service tag needs `lazydad-github-cd`'s "LazyDad Service Tag
    Reader" role); otherwise the deploy removes them. The weekly **Check Cloudflare Ranges** workflow opens an issue
    when the `appsettings.json` list no longer matches.
  - `lazydad-app-staging` (environment `lazydad-cae`): 0–1 replicas (scales to zero when idle). Calls the real LLMs.
    **Ingress allows listed IPs only** (the owner's `home` rule; Deploy Master adds its runner temporarily),
    so stray visitors can't wake it and spend LLM tokens.
  - All pull from ACR with the `lazydad-acr-pull` managed identity; the ACR admin user is disabled.
  - **Two repositories, repository permissions** (the registry's "RBAC Registry + ABAC Repository Permissions" mode):
    every build lands in `lazydad-preview`, which staging runs from and which is all `lazydad-github-staging` can
    write; production runs only from `lazydad`, which only `lazydad-github-cd` can write. Production's deploy copies
    the tested digest from `lazydad-preview` and checks it's unchanged. So a previewed branch can't move production's
    images or protection tags (runbook sections 2 and 6). Legacy `AcrPull`/`AcrPush` don't work in this mode, and the
    purge task needs its own identity with a repository role.
- Port exposed by the container: **8080**, the .NET base image's default (`ASPNETCORE_HTTP_PORTS=8080`); don't set `ASPNETCORE_URLS` too, or the app warns at every start
- **The container runs as the base image's non-root `app` user** (`$APP_UID`). It owns only `wwwroot/index.html`, which the
  image creates empty and the app writes in place at startup (it can't create or rename files in `wwwroot`). The rest of
  `/app` is root's and read-only to it. Anything else the app writes at runtime needs a writable place (its home,
  `/home/app`, or `/tmp`).
- **Version metadata is baked into the image.** Build Image (`build-image.yml`) passes build args, and the Dockerfile turns
  them into `App__Version` (short SHA), `App__Revision` (full SHA), `App__CommitDate`, `App__SourceUrl`
  (`AppInfoOptions`) and the standard OCI labels. The page shows **CalVer + SHA** under the Top 3,
  e.g. `v2026.09.25 e33d99a`, linked to the commit (`vdev (local build)` otherwise).
  Deploys remove any `App__Version` container setting, so the image is the only source.
- `/healthz` returns `{status, version, revision}`: `version` is the image commit (short SHA),
  `revision` is the platform's `CONTAINER_APP_REVISION`, unique per rollout. Smoke tests wait for both; Traffic
  Manager's health checks expect its `200`.
- `/status` returns the version, revision and this process's last scheduler tick per language
  (succeeded, saved joke ids and models, leaderboard outcome, and the error type only, no details). It lists only jokes
  that still exist (a saved one can be deleted later as a duplicate copy, by any replica); without the database, all.
  It also says whether the sign-in cookies' key ring works (`signIn.keyRing`).
- **Monitoring:** a Grafana Cloud stack (free tier, `eu-north`) gets all three apps' telemetry, one service per app
  (`job="lazydad-app"`, `"lazydad-app-swedencentral"`, `"lazydad-app-staging"`), with the uptime check and email alerts
  on prod (per app, plus `LazyDadAppNotReporting` when an app sends nothing for 10 minutes). The dashboard is
  `infra/grafana/lazydad-dashboard.json` and the alert rules `infra/grafana/lazydad-alert-rules.yaml` (both imported by hand; keep them in sync with metric and label names).
  `SchedulerMetrics.Initialize` starts every scheduler series at 0 when the scheduler starts, and `ExportNowAsync`
  sends those zeros before the first tick (the regular export is once a minute), so `increase()` also counts the first
  tick after a replica start. Request panels leave out `/healthz` (the uptime checks), which only traces filter. The OTLP credentials are
  Key Vault secrets that the apps reference with their identities: `OtlpHeaders` for prod (read as `lazydad-production`), and
  `OtlpHeadersStaging`, a separate Grafana token, for staging (runbook section 11). Each app can read only its own
  secret, so branch previews (which run on staging) can't read prod's token. Console logs also stay in the environment's Log Analytics workspace (30 days,
  daily cap) as the fallback.
- **Setup runbook:** `infra/deployment-setup.md` (bash, with a PowerShell 7 version of each block) creates everything behind Deploy Master from an
  empty subscription, in order, with managed identities and OIDC throughout (no database password, API
  key, connection-string secret or GitHub secret; the only credentials kept are in Key Vault: the Grafana tokens and the origin
  certificate), plus the operations (token rotation, the ingress ranges, taking a region out, manual
  rollback). That includes the weekly registry purge task (`purge-old-images`, in both repositories, with its own
  identity: keeps the last 30 days, 10 older
  images, and what each app runs: revisions are pinned to the image digest, and the manifest
  stays tagged `deployed-<env>` / `deploying-<env>` (`<env>-swedencentral` for production's second app) in the environment's
  repository, applied in two phases around each rollout; `previous-<env>`
  keeps what served before the latest rollout from the purge, for a manual rollback).

## Pull requests

- **Every change to an open PR is a new commit on top.** Don't amend, squash or force-push commits that are already
  pushed: whoever is reviewing or has the branch checked out keeps a stable history. Squashing, if wanted, happens at
  merge.
- **To bring a PR up to date with `master`, merge `master` into its branch** (a merge commit); don't rebase. `master`
  requires branches to be up to date before merging.

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
   `lazydad-preview:<short-sha>`, pushes it to ACR (outputting its digest), and builds the EF migration bundle
   (`dotnet-ef`, pinned in `dotnet-tools.json`).
2. **staging**, then **production** (`lazydad-app`, which migrates) and then **production-swedencentral**
   (`lazydad-app-swedencentral`, the same database, so no migrations; only after the first passed): the same reusable
   `.github/workflows/deploy-environment.yml` (**Deploy Environment**) for each app. It opens the SQL firewall for the
   runner, runs the bundle, closes the firewall,
   for production copies the image into `lazydad` (same digest, checked), protects the running and the new image with
   tags in the environment's repository (per app: the `instance` input adds `-swedencentral`), rolls the app to the
   image **by digest**
   (its version metadata is baked in; any old `App__Version` setting is removed), moves
   `deployed-<environment>` to it, syncs the ingress rules (production's Cloudflare and Traffic Manager ranges),
   allows the runner through the app's IP
   restrictions if it has any (staging's `home` rule, production's ranges), and runs `tests/LazyDad.SmokeTests`
   against it. If they fail, it restarts the new revision (a fresh startup tick) and runs them once more,
   counting only the restarted process (`/status` reports a per-process id; the retry passes the old one as `SMOKE_NOT_PROCESS`).
3. **roll-back** / **roll-back-swedencentral**, only if a production app failed **after its new revision took
   traffic**: the reusable `.github/workflows/roll-back.yml` (**Roll Back**), once per app, puts back the image that
   served before: the digest that app's job read from its own revisions before the rollout (a job output, not the
   movable `previous-<environment>` tag). Both apps serve `lazydad.fyi`, so they go back together: West Europe also when
   only Sweden Central failed. Each does nothing if its app never switched to the new revision, or if the same image
   served before. It doesn't roll back migrations, and the run still ends as failed, so GitHub notifies you.

Promotion is automatic: production runs only if staging's smoke tests pass, Sweden Central only if West Europe's
pass. Azure login is
OIDC through a managed identity per environment, each trusted via GitHub's immutable subject for its environment
only (`repo:mykolad@<id>/lazydad@<id>:environment:<env>`), with `AZURE_CLIENT_ID` set per environment:
`lazydad-github-cd` deploys production, `lazydad-github-staging` staging and the builds (runbook section 6). The
staging identity can't change production, since branch previews run as it. No secrets live in GitHub: migrations
log in to the database with Entra ID as the environment's deploy identity, and no workflow reads or passes on a
secret (no `secrets: inherit`). Production only accepts deployments
from `master`.

The smoke tests check that `/healthz` reports the new version and revision, that the page and API are served,
that the new revision itself saved a joke in every enabled language, from any of its configured models (it lists each
joke on `/status` as soon as it's saved; DB rows alone could come from the draining revision), that those jokes are in
`/jokes`, that
the leaderboard is populated with valid ranks, that `app.js`/`app.css`, `/jokes/feed` and `/jokes/summary`
are served, that a joke's page (`/j/<id>`) carries its link-preview tags and `/jokes/<id>/similar` answers, that the vote endpoint answers (with a no-op vote, so it never changes the counts), that `/me` answers signed out
(and `no-store`), and that the new revision's key ring check is `ok` (every deployed app has sign-in configured, so `off` fails too).
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
  `staging` also accepts `*/*` branches (`feature/…`, `fix/…`). The branch's own code (workflows, build) runs
  with staging's identity, which has no rights on production (runbook section 6), and Roll Back only trusts
  images production's own revisions ran. Staging's identity can only push to `lazydad-preview`, never to production's
  `lazydad` repository.
- **Migrations are off by default** (the *run-migrations* checkbox). Staging keeps any migration it
  applies, so only tick it for a branch whose migrations you'll merge unchanged.
- **It shares the `deploy` concurrency group with Deploy Master,** so the two never interleave on
  staging. The next Deploy Master run puts staging back on `master`.

### Roll Back Production

`.github/workflows/roll-back-production.yml` (**Roll Back Production**) is manual too: **Actions → Roll Back
Production → Run workflow** on `master`. It puts both production apps back on an earlier master build without
rebuilding, side by side.
- **Which version:** the *version* input (the short SHA shown on the page). Left empty, it's, for each app, the image
  of its most recent earlier revision that ran a different image, i.e. "undo the last deploy".
- **What it runs:** the reusable Roll Back workflow (shared with Deploy Master's automatic rollback), once per app. Its
  resolve job finds the image's digest and reads the commit from the image's label.
  Then the app runs the same Deploy Environment steps (protection tags, rollout by digest, that commit's
  smoke tests), **without migrations**. The database keeps its current schema, so the older code must work
  with it (additive migrations do).
- **What it refuses:** images the purge has deleted, images built before the version was baked in (#17), images no
  production app's revisions ran, commits that aren't on master, and the image the app already runs.
- **The next Deploy Master run rolls forward again.** To stay on the old version, revert on master.
- Its resolve job logs in through the `production` environment, so each rollback shows an extra
  production deployment in GitHub.

### Load Test Environment

`.github/workflows/load-test-environment.yml` (**Load Test Environment**) is manual, from any branch: it builds the
branch's image (into `lazydad-loadtest`), creates a Basic SQL database (prod's tier) on `lazydad-sql-loadtest` with
synthetic jokes (`tests/load/Seed.cs`; the *jokes* input), and runs `lazydad-app-loadtest` in `lazydad-cae` at prod's
size with the *replicas* input, the scheduler off, the vote limit raised, only the owner's IP admitted (copied from
staging's `home` rule, never written to the repo or logs) and telemetry to Grafana with staging's token (no alerts). Everything is in
`lazydad-loadtest-rg`, all `lazydad-github-loadtest` can change. The run then waits at tear-down for the owner's
approval (the `loadtest-teardown` environment). The load comes from the owner's machine: `k6 run -e BASE_URL=…
tests/load/visitors.js`, which plays visitors (page, then read about 15 s, vote twice, scroll; up to 10 scrolls) in
growing steps until p95 > 2 s or errors > 2%. Runbook section 12, "Load test".

`tsg/redeploy.ps1` is only a manual fallback now. It skips staging, migrations and smoke tests.

## Docker

```
docker build -t lazydad .
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="..." \
  -e LlmProviders__AzureOpenAI__Endpoint="..." \
  -e AZURE_TENANT_ID="..." -e AZURE_CLIENT_ID="..." -e AZURE_CLIENT_SECRET="..." \
  lazydad
```

The container has no Azure CLI and can't see your `az login`, so it authenticates through
`DefaultAzureCredential`'s environment credential: a **dev service principal** (`az ad sp create-for-rbac`; keep its
secret out of the repo) with the "Foundry User" role on `lazydad-openai-resource` and, since SQL is Entra-only, a user
in the database it connects to (`CREATE USER [<sp name>] FROM EXTERNAL PROVIDER`, with the `Active Directory Default`
connection string). For everyday local work, `dotnet run` with your `az login` needs neither.
