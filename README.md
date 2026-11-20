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

## Work order lifecycle (core)

Code is in `src/EquipTrack.Api/WorkOrders/`.

- `Lifecycle.cs` is the state machine. Each action (`AssignWarehouse`, `PassPostQaqc`, `MarkShipped`, …) lists the stages it can start from, the stage it moves to, and the roles allowed to perform it. Warehouse and dispatch personnel can act only on orders assigned to them. Anything not listed is refused with Forbidden (403), InvalidState (409) or Invalid (400).
- Each successful action appends one row to `work_order_events`: who did it, their role, the stage before and after, the named owner, and any reason or reference. A database trigger blocks UPDATE and DELETE on that table, so the history is a tamper-proof audit trail.
- `Sla.cs` calculates elapsed time from `StageStartedAt`, never from a stored counter. The stage clock restarts only when the stage actually changes; reassigning an order does not reset it.
- `FrontendContractTests` checks that the roles, the 16 stages (in order) and the stage SLA hours match the frontend's `roles.ts`, `lifecycle.ts` and `sla.ts`.

## Sign-in and permissions

- **Who you are:** users sign in with Neon Auth (email and password) in the frontend, then send `Authorization: Bearer <JWT>` to this API. `Auth/NeonJwtValidator.cs` checks the EdDSA (Ed25519) signature against Neon's JWKS, then the issuer, audience (both the Auth URL's origin), `exp`, `nbf` and the banned flag. Any other algorithm is rejected. Tokens last 15 minutes.
- **What you may do:** roles come from `organization_members`, never from the token. An org admin invites people by email and role (`POST /api/org/members`). An invitation attaches to a Neon Auth account only when that account's email is verified.
- **Bootstrap:** emails listed in `NeonAuth__SysadminEmails` (verified) can create an organisation and its first org admin (`POST /api/admin/organizations`).
- **Visibility:** each organisation sees only its own orders. Requesters see the orders they raised, warehouse and dispatch personnel see the orders assigned to them, and supervisors, QAQC, executives and logistics see every order in the organisation. Requests for anything outside a user's view return 404.

| Endpoint | Purpose |
|---|---|
| `GET /api/me` | Identity, organisation and role |
| `POST /api/admin/organizations` | Sysadmin: create an organisation and invite its admin |
| `GET/POST /api/org/members`, `PATCH /api/org/members/{id}` | List members, invite, change role, suspend |
| `POST /api/work-orders` | Requester submits an order |
| `GET /api/work-orders?status=&stage=&mine=` | Orders visible to the caller, with SLA |
| `GET /api/work-orders/{id}` | Detail, history and the actions available to the caller |
| `POST /api/work-orders/{id}/actions` | `{ action, expectedStage?, reason?, assigneeMemberId?, containerRef?, waybillNumber?, vesselName? }` |

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
| `NeonAuth__BaseUrl` | Neon Auth URL; JWKS, issuer and audience are derived from it |
| `NeonAuth__SysadminEmails__0` | Verified email(s) allowed to create organisations |
| `ForwardedHeaders__Enabled` | `true` behind a hosting proxy (Render), so rate limiting sees the real client IP |

## Deploy (Neon + Render)

- **Database:** Neon project `equiptrack` in `aws-eu-central-1` (Frankfurt), database `equiptrack`. Use the **pooled** connection string, converted to Npgsql keyword format: `Host=...-pooler...neon.tech;Port=5432;Database=equiptrack;Username=...;Password=...;SSL Mode=Require`. Npgsql does not accept `postgresql://` URLs.
- **API:** `render.yaml` is a Render Blueprint (Docker, Frankfurt, health check `/health`). In Render, choose New → Blueprint → this repo, and enter the secret values when asked. Migrations run on startup (`Database__MigrateOnStartup=true`).
- **Frontend:** set `NEXT_PUBLIC_API_BASE_URL` to the Render URL, and add the frontend's URL to `Cors__AllowedOrigins__0`.

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
