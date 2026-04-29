# Driftworld

A shared evolving world. Users make one decision per day; a global state drifts based on collective behavior; events fire when thresholds are crossed.

This is a learning project, built phase-by-phase. See [docs/00-mvp-plan.md](docs/00-mvp-plan.md) for the master plan and [docs/phase-1-skeleton-and-data-model.md](docs/phase-1-skeleton-and-data-model.md) for the current phase.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (Docker Desktop on Windows/Mac, or Docker Engine on Linux)
- The EF Core CLI tool: `dotnet tool install --global dotnet-ef --version 10.*`

## Project Structure

```
Driftworld/
  docker-compose.yml
  src/
    Driftworld.Core/    # Pure domain: WorldOptions, types (no DB, no ASP)
    Driftworld.Data/    # EF Core DbContext, entities, migrations, seed
    Driftworld.Api/     # ASP.NET Core minimal-API host
    Driftworld.Worker/  # Console app for cycle-close (Phase 3+)
  tests/
    Driftworld.Core.Tests/
    Driftworld.Api.Tests/
    Driftworld.Worker.Tests/
```

## Run Locally

### 1. Bring up Postgres

```bash
docker compose up -d
```

This starts `postgres:16` on host port **5433** (deliberately not 5432 to avoid colliding with NCache or system Postgres). Data persists in the named volume `driftworld-pgdata` across restarts.

Tear down with:
```bash
docker compose down        # stop, keep data
docker compose down -v     # stop, wipe data
```

### 2. Apply migrations

```bash
dotnet ef database update \
  --project src/Driftworld.Data \
  --startup-project src/Driftworld.Api
```

Creates the 5 tables (`users`, `cycles`, `decisions`, `world_states`, `events`) and all indexes.

### 3. Seed the genesis world state

```bash
dotnet run --project src/Driftworld.Api -- --seed
```

Inserts cycle 1 (closed, the genesis snapshot at all-50) and cycle 2 (open, the first cycle users will submit to). Idempotent — running twice is a no-op.

### 4. Run the API

```bash
dotnet run --project src/Driftworld.Api
```

Phase 1 has no real endpoints yet — just a `GET /` health stub. Real endpoints (`POST /v1/decisions`, `GET /v1/world/current`, etc.) come in Phase 2.

### 5. Run tests

```bash
dotnet test
```

## Configuration

All world parameters live in `src/Driftworld.Api/appsettings.json` under `Driftworld:World`:

- `K` — drift sensitivity (default 2)
- `Choices` — choice → delta-vector map for the three world variables
- `Rules` — threshold rules that fire events

Misconfiguration fails the host at startup, not at first cycle close. To verify: delete `K` from the config and run the API — it will refuse to start with a clear error.

## Connection String

The default connection string is in `src/Driftworld.Api/appsettings.json`. For real environments override via env var:

```bash
export ConnectionStrings__Driftworld="Host=...;Port=...;Database=...;Username=...;Password=..."
```

For local dev with the bundled compose file, no override is needed.

## Phase Status

| Phase | Status | Doc |
| ----- | ------ | --- |
| 1 — Local skeleton & data model       | ✅ | [phase-1-skeleton-and-data-model.md](docs/phase-1-skeleton-and-data-model.md) |
| 2 — User & decision endpoints         | ⬜ | not started |
| 3 — Cycle-close worker (manual)       | ⬜ | not started |
| 4 — Events & read endpoints           | ⬜ | not started |
| 5 — Scheduling, polish, hand-off      | ⬜ | not started |
