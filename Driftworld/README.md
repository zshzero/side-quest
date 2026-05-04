# Driftworld

A shared evolving world. Users make one decision per day; a global state drifts based on collective behavior; events fire when thresholds are crossed.

This is a learning project, built phase-by-phase. See [docs/00-mvp-plan.md](docs/00-mvp-plan.md) for the master plan and the per-phase docs in `docs/` for the current build state.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (Docker Desktop on Windows/Mac, or Docker Engine on Linux)
- The EF Core CLI tool: `dotnet tool install --global dotnet-ef --version 10.*`

## Project Structure

```
Driftworld/
  Directory.Build.props        # shared MSBuild props + CPM enable
  Directory.Packages.props     # central package versions
  docker-compose.yml
  src/
    Driftworld.Core/    # Pure domain: WorldOptions, exceptions, types
    Driftworld.Data/    # EF Core DbContext, entities, migrations, seed
    Driftworld.Api/     # ASP.NET Core minimal-API host
    Driftworld.Worker/  # Console app for cycle-close (Phase 3+)
  tests/
    Directory.Build.props      # shared test-project props
    Driftworld.Core.Tests/
    Driftworld.Data.Tests/
    Driftworld.Api.Tests/
    Driftworld.Worker.Tests/
```

## Run Locally

### 1. Bring up Postgres

```bash
docker compose up -d
```

`postgres:16` on host port **5433**. Data persists in the named volume `driftworld-pgdata`.

### 2. Apply migrations

```bash
dotnet ef database update --project src/Driftworld.Data --startup-project src/Driftworld.Api
```

### 3. Seed the genesis world state

```bash
dotnet run --project src/Driftworld.Api -- --seed
```

Inserts cycle 1 (closed, all-50 genesis snapshot) and cycle 2 (open, the first cycle users submit to). Idempotent.

### 4. Run the API

```bash
dotnet run --project src/Driftworld.Api
# → http://localhost:5059  (or whatever launchSettings.json says)
```

### 4b. Advance the world (manually)

```bash
dotnet run --project src/Driftworld.Worker
```

Closes any cycle whose `ends_at` is in the past, computes a new `world_states` row from that cycle's decisions, and opens a successor. **Idempotent** — running twice when no cycle is due is a no-op. **Multi-day catch-up** — if the worker hasn't run for several days, a single invocation closes all overdue cycles in order.

In production this is fired by OS-level cron / Task Scheduler (see "Scheduling the worker" below). For local dev, invoke whenever you want to advance time.

### 5. Run tests

```bash
dotnet test
```

## API (Phase 2)

All errors return `application/problem+json` per RFC 7807, with the domain `code` in the response body's `extensions`.

### `POST /v1/users`

Create a user. Handle is optional (anonymous users have `handle: null`).

```bash
curl -X POST http://localhost:5080/v1/users \
  -H "Content-Type: application/json" \
  -d '{"handle": "ada"}'

# 201 Created
# { "userId": "8f3...", "handle": "ada" }
```

```bash
# Anonymous user
curl -X POST http://localhost:5080/v1/users -H "Content-Type: application/json" -d '{}'
# 201 Created
# { "userId": "...", "handle": null }
```

Errors:
- `400 invalid_handle` — handle present but fails validation (3–32 chars, `[a-zA-Z0-9_-]+`)
- `409 duplicate_handle` — handle already taken

### `POST /v1/decisions`

Submit one decision for the currently open cycle. Requires `X-User-Id` header.

```bash
curl -X POST http://localhost:5080/v1/decisions \
  -H "X-User-Id: 8f3..." \
  -H "Content-Type: application/json" \
  -d '{"choice": "build"}'

# 201 Created
# { "decisionId": "a1b...", "cycleId": 2 }
```

Valid choices come from `appsettings.json` → `Driftworld:World:Choices`. Lookup is case-insensitive.

Errors:
- `401 missing_user_id` — `X-User-Id` header absent
- `400 malformed_user_id` — `X-User-Id` is not a UUID
- `401 unknown_user` — UUID is well-formed but no matching user
- `400 unknown_choice` — choice not in `Driftworld:World:Choices`
- `409 duplicate` — same user already decided in this cycle (`extensions.cycle_id` set)
- `503 no_open_cycle` — should never happen in normal operation; means the cycle-close worker has failed

### `GET /v1/world/current`

Latest closed-cycle world state, plus open-cycle metadata, plus events currently active (rules whose threshold still holds).

```bash
curl http://localhost:5059/v1/world/current
# {
#   "cycle":  { "id": 3, "startsAt": "...", "endsAt": "...", "status": "open" },
#   "state":  { "economy": 19, "environment": 50, "stability": 50, "asOfCycleId": 2 },
#   "activeEvents": [{ "type": "recession", "sinceCycleId": 2 }]
# }
```

`activeEvents` is computed by re-evaluating every rule against the latest closed state at read time. `sinceCycleId` is the earliest cycle (walking backward) where the rule was continuously holding.

### `GET /v1/world/history?limit=N`

Recent closed-cycle states, descending by `cycleId`. Default limit 30, max 365.

```bash
curl 'http://localhost:5059/v1/world/history?limit=10'
# { "items": [ { "cycleId": 3, "economy": 19, ... }, ... ] }
```

Errors:
- `400 invalid_limit` — `limit` outside `[1, 365]`

### `GET /v1/events`

Triggered events. **Either** `cycle_id` or `limit`, not both. Default limit 30, max 200.

```bash
curl 'http://localhost:5059/v1/events?cycle_id=2'
curl 'http://localhost:5059/v1/events?limit=20'
# { "items": [ { "cycleId": 2, "type": "recession", "payload": { ... }, "createdAt": "..." } ] }
```

Errors:
- `400 conflicting_filters` — both `cycle_id` and `limit` supplied
- `400 invalid_limit` — `limit` outside `[1, 200]`

### `GET /v1/users/{id}/contribution`

Per-user totals + alignment with the modal choice across cycles.

```bash
curl http://localhost:5059/v1/users/.../contribution
# {
#   "userId": "...",
#   "totalDecisions": 17,
#   "byChoice": { "build": 9, "preserve": 5, "stabilize": 3 },
#   "alignment": { "withMajorityPct": 64 }
# }
```

`alignment.withMajorityPct` is the % of cycles in which the user's choice matched the modal choice across all decisions in that cycle. Ties are broken alphabetically. Public — no `X-User-Id` required (id is in the path).

Errors:
- `401 unknown_user` — no user with that id

## Scheduling the worker

The cycle-close worker is a separate process. In production it's fired daily by an OS-level scheduler. Two notes that catch people:
- The worker's `closed_at` calculation pins to *nominal* close time, so even a multi-day-late catch-up records history correctly. **Don't** worry about cron drift — the worker tolerates it.
- `Host.CreateApplicationBuilder` defaults to `DOTNET_ENVIRONMENT=Production` if the env var isn't set. **Always set it explicitly** in your scheduler entry to avoid a dev-only override sneaking into production.

### Linux cron

```
# Run at 00:05 UTC daily.
5 0 * * * cd /opt/driftworld && DOTNET_ENVIRONMENT=Production \
  /usr/bin/dotnet src/Driftworld.Worker/bin/Release/net10.0/Driftworld.Worker.dll \
  >> logs/cron-worker.log 2>&1
```

### Windows Task Scheduler

One-liner via `schtasks`:

```cmd
schtasks /create /sc daily /st 00:05 /tn "DriftworldWorker" ^
  /tr "cmd /c cd /d C:\driftworld && set DOTNET_ENVIRONMENT=Production && dotnet src\Driftworld.Worker\bin\Release\net10.0\Driftworld.Worker.dll >> logs\schedule.log 2>&1"
```

For local dev / one-off invocations: `dotnet run --project src/Driftworld.Worker`.

## Logs

Both the API and the Worker log to console + a `logs/` directory **relative to the current working directory**. Daily rolling, 7-day retention. Files are named `driftworld-api-YYYYMMDD.log` and `driftworld-worker-YYYYMMDD.log`. For predictable paths in production, your scheduler entry should `cd` to a known root before running (the cron / `schtasks` snippets below already do this).

## Rate limiting

`POST /v1/users` is rate-limited per IP — 5 requests per 60-second window by default. The 6th request returns `429 Too Many Requests` as `application/problem+json` with `extensions.code = "rate_limit_exceeded"` and `extensions.retry_after_seconds`.

Tunable via `appsettings.json`:

```json
"Driftworld": {
  "RateLimit": {
    "UserCreate": { "PermitLimit": 5, "WindowSeconds": 60 }
  }
}
```

If you ever deploy behind a reverse proxy or load balancer, you'll need to enable `UseForwardedHeaders` so per-IP partitioning sees the real client IP rather than the proxy's. See `docs/phase-5-scheduling-and-polish.md` §1.4 for the trust-config you'll want.

## Configuration

All world parameters live in `src/Driftworld.Api/appsettings.json` under `Driftworld:World`:

- `K` — drift sensitivity (default 2)
- `Choices` — choice → delta-vector map for the three world variables
- `Rules` — threshold rules that fire events (Phase 4)

Misconfiguration fails the host at startup. To verify: delete `K` and run the API — it refuses to start with a clear `OptionsValidationException`.

## Connection String

Default in `src/Driftworld.Api/appsettings.Development.json`. Override via env var:

```bash
export ConnectionStrings__Driftworld="Host=...;Port=...;Database=...;Username=...;Password=..."
```

Local dev with the bundled compose file needs no override.

## Phase Status

| Phase | Status | Doc |
| ----- | ------ | --- |
| 1 — Local skeleton & data model       | ✅ done   | [phase-1-skeleton-and-data-model.md](docs/phase-1-skeleton-and-data-model.md) |
| 1.5 — Reviewer-driven punch-list      | ✅ done   | (folded into Phase 1 doc + tests) |
| 2 — Users & decisions endpoints       | ✅ done   | [phase-2-users-and-decisions.md](docs/phase-2-users-and-decisions.md) |
| 3 — Cycle-close worker (manual)       | ✅ done   | [phase-3-cycle-close-worker.md](docs/phase-3-cycle-close-worker.md) |
| 4 — Events & read endpoints           | ✅ done   | [phase-4-events-and-reads.md](docs/phase-4-events-and-reads.md) |
| 5 — Scheduling, polish, hand-off      | ✅ done   | [phase-5-scheduling-and-polish.md](docs/phase-5-scheduling-and-polish.md) |
