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
# → http://localhost:5080  (or whatever launchSettings.json says)
```

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
| 3 — Cycle-close worker (manual)       | ⬜ next   | not started |
| 4 — Events & read endpoints           | ⬜       | not started |
| 5 — Scheduling, polish, hand-off      | ⬜       | not started |
