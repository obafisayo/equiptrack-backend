# EquipTrack API (ASP.NET Core 8)

The server side of EquipTrack. The Next.js frontend lives in a separate repository, [equiptrack-solution](https://github.com/obafisayo/equiptrack-solution). Right now it only handles **lead capture** (the waitlist and newsletter forms). It is the base that the core lifecycle API, real logins and server-side SLA timers will be built on.

## Layout

```
equiptrack-backend/
  EquipTrack.sln
  src/EquipTrack.Api/
    Program.cs            composition root: DB, CORS, rate limiting, health
    Domain/Roles.cs       canonical role codes (must match equiptrack-solution's src/lib/domain/roles.ts)
    Leads/                endpoints, validation, outbox processor + worker
    Email/                Brevo client, options, email templates
    Data/                 EF Core DbContext + migrations (PostgreSQL)
  tests/EquipTrack.Api.Tests/
```

## How lead capture works

1. `POST /api/leads/waitlist` or `POST /api/leads/newsletter` validates the input and **stores the lead in Postgres**. The response is `202 Accepted`.
2. `LeadSyncWorker` picks up pending leads and, through Brevo:
   - creates or updates the contact and adds it to the waitlist or newsletter list
   - sends the confirmation email to the lead
   - for waitlist leads, emails the internal team
3. Each step is recorded as it succeeds, so a retry never sends an email twice. Failures retry with exponential backoff. After `LeadSync:MaxAttempts` the lead is marked `Failed` and keeps its `LastSyncError`.

If Brevo is not configured, leads are still stored and sync once it is.

## Run locally

Requires the .NET 8 SDK and Docker.

```bash
docker compose up -d                                # Postgres on :5432
dotnet run --project src/EquipTrack.Api             # http://localhost:5080 (migrates on start in Development)
```

Then in the frontend repo (`equiptrack-solution`): `cp .env.example .env.local && npm run dev`. The frontend calls this API at `NEXT_PUBLIC_API_BASE_URL`.

## Configuration

Set secrets as environment variables, never in `appsettings.json`:

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Default` | Postgres connection string |
| `Database__MigrateOnStartup` | `true` to apply migrations on boot |
| `Cors__AllowedOrigins__0` | Frontend origin, e.g. `https://equiptrack.app` |
| `Brevo__ApiKey` | Brevo v3 API key |
| `Brevo__SenderEmail` / `Brevo__SenderName` | A sender verified in Brevo |
| `Brevo__TeamNotificationEmail` | Inbox that receives new waitlist applications |
| `Brevo__WaitlistListId` / `Brevo__NewsletterListId` | Brevo contact list IDs (0 = no list) |
| `RateLimiting__LeadsPerMinute` | Per-IP limit on lead endpoints (default 10) |

If you deploy behind a reverse proxy, configure forwarded headers before you rely on the per-IP rate limit.

## Tests and migrations

```bash
dotnet test
dotnet tool restore
dotnet ef migrations add <Name> -p src/EquipTrack.Api -o Data/Migrations
```

CI fails if the EF model changes without a matching migration.

## Role contract with the frontend

`RoleContractTests` compares `Domain/Roles.cs` with the frontend's `src/lib/domain/roles.ts`. It finds the frontend through `FRONTEND_REPO_PATH`, or through a sibling checkout at `../equiptrack-solution`. CI checks out the frontend repo and sets `REQUIRE_ROLE_CONTRACT=true`, so a missing or mismatched list fails the build. If `equiptrack-solution` is private, add a repository secret `FRONTEND_REPO_TOKEN`: a fine-grained token with read access to that repo.

CI compares against the frontend's `master` branch by default. To check against another branch, for example while a frontend role change is still in review, set the repository variable `FRONTEND_REF` (Settings → Secrets and variables → Actions → Variables), or pass `frontend_ref` when you run the workflow manually.

When you change roles, update both repositories in the same change.
