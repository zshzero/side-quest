# Driftworld — MVP Plan

A shared evolving world. Users make one decision per day; a global state drifts based on aggregate behavior; events fire when thresholds are crossed.

This document is the master plan. Each phase below will get its own deep-dive learning doc (`phase-N-*.md`) **before** that phase is implemented — same pattern as NCache.

---

## 1. Stack (chosen, no ambiguity)

| Layer            | Choice                                    | Why                                                       |
| ---------------- | ----------------------------------------- | --------------------------------------------------------- |
| Runtime          | .NET 10 (LTS)                             | Current LTS; day-job familiarity                          |
| API              | ASP.NET Core 10 Minimal APIs              | Light, fast to build, good for REST                       |
| ORM              | EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` | Migrations + LINQ; small enough model that EF is fine    |
| DB               | PostgreSQL 16                             | Stated requirement                                        |
| Worker           | Console app (`Driftworld.Worker`)         | Same codebase, separate process — invokable by cron/Task Scheduler |
| Tests            | xUnit + FluentAssertions + Testcontainers | Real Postgres for integration tests                       |
| Local infra      | Docker Compose (Postgres only)            | One command to bring up DB                                |
| Scheduler (prod) | OS-level cron / Windows Task Scheduler    | Avoids in-process scheduling fragility                    |
| Caching          | None for MVP                              | Reads are trivial; add Redis only if measurements demand  |

---

## 2. Repository layout (mirrors NCache)

```
Driftworld/
├─ Driftworld.sln
├─ docker-compose.yml          # postgres only
├─ README.md                   # how to run locally
├─ docs/
│  ├─ 00-mvp-plan.md           # this file
│  ├─ phase-1-*.md             # written before phase 1 starts
│  ├─ phase-2-*.md
│  └─ ...
├─ src/
│  ├─ Driftworld.Api/          # ASP.NET Core minimal-API host
│  ├─ Driftworld.Worker/       # console app — cycle-close
│  ├─ Driftworld.Core/         # pure domain: aggregation, rules, types
│  └─ Driftworld.Data/         # EF Core DbContext + migrations
└─ tests/
   ├─ Driftworld.Core.Tests/   # pure unit tests (no DB)
   ├─ Driftworld.Api.Tests/    # WebApplicationFactory + Testcontainers
   └─ Driftworld.Worker.Tests/ # worker against real Postgres
```

Why split `Core` from `Data`/`Api`/`Worker`: aggregation math and rule evaluation are pure functions and must be unit-testable without a DB, and reusable from both the API (preview) and the worker (commit).

---

## 3. System architecture (recap)

```
[Client] ──HTTPS──▶ [Driftworld.Api] ──▶ [PostgreSQL] ◀── [Driftworld.Worker]
                                                              ▲
                                                              │ invoked by
                                                       cron / Task Scheduler
                                                          (00:05 UTC daily)
```

- **Online path**: user posts a decision → API validates → row in `decisions` (one per user per cycle, enforced by unique index).
- **Batch path**: worker runs once per cycle, transactionally: reads decisions → computes new world state → writes `world_states` row → evaluates rules → writes `events` rows → closes current cycle, opens next. Idempotent.
- **Read path**: clients hit `GET /v1/world/*` — plain reads from latest `world_states` row.

**Event processing model: batch, cycle-driven.** Streaming/event-sourcing is rejected as overkill for a once-per-24h tick. The cycle close *is* the natural transactional boundary.

---

## 4. Data model

All timestamps `timestamptz` UTC. IDs are `uuid` except `cycles.id` (`int`, monotonic).

### `users`
| col           | type        | notes                                  |
| ------------- | ----------- | -------------------------------------- |
| id            | uuid PK     | client stores locally after first call |
| handle        | text NULL UNIQUE | optional display name             |
| created_at    | timestamptz |                                        |
| last_seen_at  | timestamptz | bumped on auth'd requests              |

### `cycles`
| col        | type                                              | notes                              |
| ---------- | ------------------------------------------------- | ---------------------------------- |
| id         | int PK (identity)                                 |                                    |
| starts_at  | timestamptz                                       |                                    |
| ends_at    | timestamptz                                       |                                    |
| status     | text CHECK in ('open','closed')                   |                                    |
| closed_at  | timestamptz NULL                                  |                                    |

Partial unique index: `CREATE UNIQUE INDEX ix_one_open_cycle ON cycles(status) WHERE status='open';` — at most one open cycle ever.

### `decisions`
| col        | type                                                                | notes                             |
| ---------- | ------------------------------------------------------------------- | --------------------------------- |
| id         | uuid PK                                                             |                                   |
| user_id    | uuid FK → users(id)                                                 |                                   |
| cycle_id   | int FK → cycles(id)                                                 |                                   |
| choice     | text CHECK in ('build','preserve','stabilize')                      |                                   |
| created_at | timestamptz                                                         |                                   |

`UNIQUE (user_id, cycle_id)`. Index on `(cycle_id)` for aggregation reads.

### `world_states`
| col          | type     | notes                                              |
| ------------ | -------- | -------------------------------------------------- |
| cycle_id     | int PK FK → cycles(id) | one state per cycle (the *closing* state) |
| economy      | smallint | 0–100                                              |
| environment  | smallint | 0–100                                              |
| stability    | smallint | 0–100                                              |
| participants | int      | decisions counted into this state                  |
| created_at   | timestamptz |                                                 |

### `events`
| col        | type        | notes                                                    |
| ---------- | ----------- | -------------------------------------------------------- |
| id         | uuid PK     |                                                          |
| cycle_id   | int FK      |                                                          |
| type       | text        | `recession`, `ecological_collapse`, `unrest`, `golden_age` |
| payload    | jsonb       | snapshot of triggering variables                         |
| created_at | timestamptz |                                                          |

`UNIQUE (cycle_id, type)` — each event type fires at most once per cycle.

### Choice → delta vector (config, not table)

```csharp
// Driftworld.Core/Choices.cs (sketch — not code yet)
build      => (economy: +3, environment: -2, stability:  0)
preserve   => (economy: -1, environment: +3, stability:  0)
stabilize  => (economy: -1, environment:  0, stability: +3)
```

### Example records

```
users    : (a1.., 'ada',  2026-04-25 …)
cycles   : (42, 2026-04-27 00:00Z, 2026-04-28 00:00Z, 'closed', 2026-04-28 00:05:03Z)
           (43, 2026-04-28 00:00Z, 2026-04-29 00:00Z, 'open',   NULL)
decisions: (d1.., a1.., 43, 'build', 2026-04-28 09:11Z)
world_states: (42, 53, 47, 50, 128, 2026-04-28 00:05:04Z)
events   : (e1.., 42, 'recession', {"economy":18}, 2026-04-28 00:05:04Z)
```

### Genesis seed (resolved)

The seed produces exactly two cycle rows and one world-state row:

| row              | values                                                                                                  |
| ---------------- | ------------------------------------------------------------------------------------------------------- |
| `cycles` 1       | `closed`, `starts_at = T-24h`, `ends_at = T0`, `closed_at = T0` — the genesis snapshot's owning cycle |
| `cycles` 2       | `open`,   `starts_at = T0`,    `ends_at = T0+24h`, `closed_at = NULL` — the first cycle users submit to |
| `world_states` 1 | `cycle_id = 1`, all variables `50`, `participants = 0`                                                  |

`T0` is the UTC midnight at-or-before seed time. There is no "cycle 0" — `cycles.id` is identity starting at 1, and the genesis state belongs to cycle 1. `GET /v1/world/current` immediately after seed returns cycle 2's metadata as `open` and cycle 1's state as `as_of_cycle_id`.

---

## 5. Core logic

### Aggregation (deterministic)

For each variable `v`:

```
sum_delta_v = Σ delta_v(choice_i) over decisions in cycle  (integer)
N           = count(decisions in cycle)
mean_delta  = N == 0 ? 0.0 : sum_delta_v / N              (decimal, not int division)
raw_v       = prev_v + K * mean_delta                     (decimal)
new_v       = clamp(round_half_away_from_zero(raw_v), 0, 100)   (smallint, persisted)
```

Computed in C# using `decimal` (not `double`) and `Math.Round(x, MidpointRounding.AwayFromZero)`. **No SQL-side aggregation** — integer division and float associativity would both break determinism. `K` comes from config (`Driftworld:World:K`, default 2).

Normalizing by N means **the ratio of choices drives the world, not raw turnout** — important because we don't want a Reddit raid to nuke the variables. Empty cycles → no drift.

### Configuration shape

`K`, the choice→delta vectors, and the rule thresholds are not constants in code — they live in `appsettings.json` under a single `Driftworld:World` section, bound at startup via `IOptions<WorldOptions>` and injected into both the API (preview/validation) and the worker (commit). Tuning the world doesn't require recompiling, and `Driftworld.Core` stays free of static config.

```jsonc
// appsettings.json (shape, not final values)
"Driftworld": {
  "World": {
    "K": 2,
    "Choices": {
      "build":      { "Economy":  3, "Environment": -2, "Stability":  0 },
      "preserve":   { "Economy": -1, "Environment":  3, "Stability":  0 },
      "stabilize":  { "Economy": -1, "Environment":  0, "Stability":  3 }
    },
    "Rules": {
      "recession":           { "Variable": "Economy",     "Op": "lt", "Threshold": 20 },
      "ecological_collapse": { "Variable": "Environment", "Op": "lt", "Threshold": 15 },
      "unrest":              { "Variable": "Stability",   "Op": "lt", "Threshold": 20 },
      "golden_age":          { "All": [
          { "Variable": "Economy",     "Op": "gte", "Threshold": 70 },
          { "Variable": "Environment", "Op": "gte", "Threshold": 70 },
          { "Variable": "Stability",   "Op": "gte", "Threshold": 70 }
      ] }
    }
  }
}
```

`WorldOptions` is validated at startup (FluentValidation or `IValidateOptions<T>`) — fail fast on a malformed config rather than at first cycle close.

### Event rules

Evaluated after the new state is computed; each rule writes 0 or 1 row, keyed by `(cycle_id, type)`:

```
recession            : economy     < 20
ecological_collapse  : environment < 15
unrest               : stability   < 20
golden_age           : economy ≥ 70 AND environment ≥ 70 AND stability ≥ 70
```

Rules are built at startup as `IReadOnlyList<IRule>` from `WorldOptions.Rules` (see "Configuration shape" above) and registered in DI. `Driftworld.Core` defines `IRule` and the rule shapes (leaf + `All`-composite) but holds no rule list of its own — adding a rule is a config edit, not a code change.

### Determinism

`AggregateAndApply(prevState, decisions, K)` and `EvaluateRules(state)` are pure functions on `Driftworld.Core` — unit-tested without time, randomness, or a DB.

---

## 6. API design

REST/JSON, versioned under `/v1`. Validation via minimal-API parameter binding + FluentValidation. Light auth: client calls `POST /v1/users` once, stores returned `user_id`, sends it as header `X-User-Id` on subsequent calls. No passwords for MVP — trivially upgraded later.

Endpoints requiring `X-User-Id`: only `POST /v1/decisions`. Missing header → `401` ProblemDetails with `code: "missing_user_id"`. Header present but not a parsable UUID → `400` with `code: "malformed_user_id"`. Header present and parsable but the user does not exist → `401` with `code: "unknown_user"`. `GET /v1/users/{id}/contribution` is **public** for MVP — no header required; the path id is the lookup.

| Method | Path                              | Purpose                                      | Pagination                          |
| ------ | --------------------------------- | -------------------------------------------- | ----------------------------------- |
| POST   | `/v1/users`                       | Create user, get `user_id`                   | —                                   |
| POST   | `/v1/decisions`                   | Submit one decision for the open cycle       | —                                   |
| GET    | `/v1/world/current`               | Latest closed state + open cycle metadata    | —                                   |
| GET    | `/v1/world/history?limit=N`       | Recent N closed states                       | default 30, max 365                 |
| GET    | `/v1/events?cycle_id=` **xor** `?limit=N`   | Triggered events                   | `limit` default 30, max 200         |
| GET    | `/v1/users/{id}/contribution`     | User's totals + alignment with majority      | —                                   |

`limit` outside its bounds → `400` ProblemDetails; clamp is *not* silent. On `GET /v1/events`, `cycle_id` and `limit` are **mutually exclusive** — supplying both → `400` with `code: "conflicting_filters"`. With neither, the default is `limit=30`.

### Error format — ProblemDetails (RFC 7807)

All non-2xx responses are `application/problem+json`, served by ASP.NET Core 10's built-in ProblemDetails middleware. The domain `code` lives in the `extensions` bag rather than the body root, keeping us standards-compliant while still machine-readable:

```json
{
  "type":     "https://driftworld/errors/duplicate-decision",
  "title":    "Decision already submitted for this cycle",
  "status":   409,
  "detail":   "User a1b2c3d4-... already decided in cycle 43.",
  "instance": "/v1/decisions",
  "code":     "duplicate",
  "cycle_id": 43
}
```

A small `IExceptionHandler` translates known domain exceptions (`DuplicateDecisionException`, `UnknownChoiceException`, `UnknownUserException`, `NoOpenCycleException`) into ProblemDetails with the right `status` + `code`. Unhandled exceptions → `500` with `code: "internal"` and no leak of internals.

All timestamps in successful responses are ISO-8601 UTC with `Z` suffix.

### Request / response examples

**`POST /v1/decisions`**
```http
POST /v1/decisions
X-User-Id: a1b2c3d4-...
Content-Type: application/json

{ "choice": "build" }

→ 201 Created
{ "decision_id": "d1...", "cycle_id": 43 }

→ 409 Conflict (application/problem+json)
{
  "type": "https://driftworld/errors/duplicate-decision",
  "title": "Decision already submitted for this cycle",
  "status": 409,
  "code": "duplicate",
  "cycle_id": 43
}
```

**`GET /v1/world/current`**
```json
{
  "cycle":  { "id": 43, "starts_at": "...", "ends_at": "...", "status": "open" },
  "state":  { "economy": 53, "environment": 47, "stability": 50, "as_of_cycle_id": 42 },
  "active_events": [{ "type": "recession", "since_cycle_id": 42 }]
}
```

#### "Active events" — defined

An event is **active** iff its rule still evaluates true against the most recently closed `world_state`. The endpoint re-runs the configured rule list at read time against that state and returns the matching `type`s. `since_cycle_id` is the earliest contiguous closed cycle, walking backwards, where the rule was also true (so a 5-cycle-long recession reports `since_cycle_id` of the cycle it started, not the latest one).

This gives us:
- Stateless "active" — no extra columns or background sweeps.
- Self-healing — if a rule is removed from config, it stops being "active" instantly.
- Cheap — at most O(R · H) where R is the rule count and H is the contiguous-streak length, both tiny in MVP.

---

## 7. Background jobs

A single CLI: `dotnet run --project src/Driftworld.Worker` (or the published binary). Cron at `5 0 * * *` UTC (5 minutes of slack for clock skew).

The worker **loops** until the open cycle hasn't ended yet — this gives free multi-day catch-up if a scheduled run was missed (laptop asleep, host down). Each loop iteration runs **in its own transaction**:

1. `SELECT … FROM cycles WHERE status='open' FOR UPDATE` — bail with exit 0 if none (impossible under normal operation; alarmable).
2. If `now() < cycle.ends_at`, `COMMIT` and exit 0 (idempotent against early invocation; this is also the loop terminator).
3. Aggregate decisions for `cycle.id` → compute new `world_states` row (per §5 formula).
4. Evaluate rules → insert `events` rows (`UNIQUE (cycle_id, type)` makes re-runs no-op).
5. `UPDATE cycles SET status='closed', closed_at=LEAST(now(), cycle.ends_at + interval '5 minutes')` — `closed_at` reflects nominal close time, not catch-up wall-clock, so backfills don't look like a 3-day-late close.
6. `INSERT INTO cycles (starts_at, ends_at, status) VALUES (cycle.ends_at, cycle.ends_at + interval '24 hours', 'open')`.
7. `COMMIT`. Loop back to step 1.

On any failure: that iteration rolls back; the loop exits with non-zero; the next scheduled invocation retries from where it stopped. Per-iteration transactions (rather than one big txn) bound rollback work and keep `FOR UPDATE` lock duration short.

**Why an OS-level scheduler, not Quartz/Hangfire/`IHostedService` cron**: API restarts don't disturb the schedule, the worker has its own logs and exit code (alertable), scaling the API horizontally later doesn't risk N workers double-firing, one fewer dependency. For local dev, the same binary is invokable manually whenever you want to advance time — *crucial* for testing.

---

## 8. Phased build plan

Five phases. Each ends with something you can demo. Each gets a learning doc in `docs/phase-N-*.md` written **before** code.

### Phase 1 — Local skeleton & data model
**Concepts to learn first**: ASP.NET Core 10 minimal-API project layout; EF Core code-first vs migrations-first; Npgsql connection strings; `IOptions<T>` + options validation; `docker compose` for ephemeral Postgres.

**Build**:
- `Driftworld.sln` with the 4 src projects + 3 test projects.
- `docker-compose.yml`: one `postgres:16` service on port 5433 (avoid colliding with NCache or default), volume `pgdata`.
- `Driftworld.Data`: `DriftworldDbContext`, entity classes, initial EF migration creating all 5 tables + indexes + the partial-unique-index on `cycles(status)`.
- Seed script per §4 "Genesis seed": cycle 1 closed (genesis snapshot), cycle 2 open, one `world_states` row with all-50 keyed to cycle 1. No "cycle 0."
- `Driftworld.Core` skeleton with `WorldOptions` (the `Driftworld:World` section, see §5 "Configuration shape") + `IValidateOptions<WorldOptions>` so a malformed `appsettings.json` fails the host at startup, not at first cycle close.
- `appsettings.json` carrying the default `K`, `Choices`, `Rules` values.
- README quickstart: `docker compose up -d`, `dotnet ef database update`, `dotnet run --project src/Driftworld.Api`.

**Done when**: `dotnet ef database update` succeeds against the dockerized Postgres, `psql` shows exactly the rows specified in §4 "Genesis seed", and the API host fails to start (with a clear validation error) if `Driftworld:World:K` is missing.

### Phase 2 — User & decision endpoints
**Concepts**: minimal-API endpoint groups, FluentValidation, ASP.NET Core 10 ProblemDetails (RFC 7807) + `IExceptionHandler`, EF Core unique-constraint violation handling, integration tests with `WebApplicationFactory` + Testcontainers.

**Build**:
- ProblemDetails wired up as the *only* error format per §6 "Error format". Domain exceptions: `DuplicateDecisionException`, `DuplicateHandleException`, `UnknownChoiceException`, `UnknownUserException`, `MissingUserIdException`, `MalformedUserIdException`, `NoOpenCycleException`. `IExceptionHandler` maps each to the right `status` + `code` (in `extensions`).
- `POST /v1/users`: body `{ "handle": string? }`. Handle rules — when present: 3–32 chars, `[a-zA-Z0-9_-]+`, unique (collision → 409 `code: "duplicate_handle"`); when omitted/null: user is created with `handle = NULL` (anonymous), no uniqueness check applies. Always returns 201 with `{ "user_id": "...", "handle": null | "..." }`.
- `POST /v1/decisions`: requires `X-User-Id` per §6 auth rules; unique-constraint on `(user_id, cycle_id)` → `DuplicateDecisionException` → 409 ProblemDetails.
- Integration tests: handle happy path, anonymous (handle omitted) happy path, duplicate handle (409), duplicate decision (asserts `application/problem+json`, `status=409`, `extensions.code="duplicate"`, `extensions.cycle_id`), missing `X-User-Id` (401), malformed `X-User-Id` (400), unknown user (401), unknown choice (400).

**Done when**: tests green; manual `curl` round-trip works; every error response is `application/problem+json`.

### Phase 3 — Cycle-close worker (manual)
**Concepts**: console-app `IHost` for DI parity with API; `SELECT ... FOR UPDATE` and serialization in EF Core / Npgsql; transaction scope; pure-function aggregation in `Driftworld.Core`.

**Build**:
- `Driftworld.Core`: `Choices`, `WorldState`, `AggregateAndApply` (using `decimal` + `MidpointRounding.AwayFromZero` per §5), fully unit-tested.
- `Driftworld.Worker`: per-cycle transaction loop (per §7) — closes overdue cycles back-to-back until the open cycle is in the future.
- Unit tests covering rounding boundaries: a mean-delta producing exactly `+0.5` rounds to `+1`, exactly `-0.5` rounds to `-1`, and saturation at `clamp(0,100)` is asserted at both ends.
- Integration test: seed → submit 5 decisions → run worker → assert exact new state by hand-calc → assert successor cycle exists → run worker again → assert no-op.
- Integration test: seed → submit decisions across 3 simulated past cycles (by manipulating `cycles.ends_at` to be in the past) → single worker invocation closes all 3 in order, leaves one open cycle in the future, all three `world_states` rows correct.

**Done when**: `dotnet run --project src/Driftworld.Worker` advances the world; double-invocation is a no-op; multi-day catch-up closes all overdue cycles in a single invocation.

### Phase 4 — Events & read endpoints
**Concepts**: rule-list pattern in C# (`IReadOnlyList<IRule>`) bound from `WorldOptions.Rules`; idempotent inserts via unique key + `ON CONFLICT DO NOTHING` (or EF equivalent); pagination caps as 400 errors, not silent clamps; stateless "active events" via read-time rule re-evaluation.

**Build**:
- `IRule` resolved from `WorldOptions.Rules` (not hardcoded), plus an `IRuleEvaluator`. The same evaluator is reused at write time (worker → `events` table) and at read time (`/world/current` → `active_events`).
- `GET /v1/world/current`: implements the §6 "Active events" semantics — re-runs the rule list against the latest closed `world_state`, computes `since_cycle_id` by walking `world_states` backwards while the rule still holds.
- `GET /v1/world/history?limit=N` (default 30, max 365; out-of-range → 400 ProblemDetails with `code: "invalid_limit"`).
- `GET /v1/events?cycle_id= or limit=` (default 30, max 200; same out-of-range handling).
- `GET /v1/users/{id}/contribution`.
- Tests: rule boundary values; pagination — `limit=0`, `limit=366` on history, `limit=201` on events all return 400; `active_events` correctness — recession rule active for 3 contiguous cycles reports `since_cycle_id` of the first; contribution math against a small fixture.

**Done when**: driving `economy` below 20 produces exactly one `recession` row in that cycle, `GET /v1/world/current` reflects it under `active_events` with the correct `since_cycle_id`, and recovering `economy` to ≥ 20 in a later cycle removes it from `active_events` without any DB write.

### Phase 5 — Scheduling, polish, hand-off
**Concepts**: Windows Task Scheduler vs cron entries; structured logging with Serilog; rate limiting via `Microsoft.AspNetCore.RateLimiting`.

**Build**:
- Per-IP rate limit on `POST /v1/users`.
- Serilog → console + rolling file in `logs/` (matches NCache convention).
- README documents both: a `cron` line for Linux and a Task Scheduler XML / `schtasks` command for Windows. Local dev still invokes manually.
- Final acceptance pass (§10).

**Done when**: every checkbox in §10 ticks on a fresh clone.

---

## 9. Edge cases (decisions, not options)

- **Duplicate submission** → DB unique → 409 ProblemDetails with `extensions.code="duplicate"`.
- **Submission landing as worker runs**: API reads the *currently open* cycle row in the same statement. If worker has already opened cycle N+1, the decision goes there. Acceptable.
- **No open cycle** (worker bug): API returns 503; alarm. Worker always opens a successor in the same txn, so this is unreachable in normal operation.
- **Empty cycle**: `mean_delta = 0`, world state copies forward unchanged, `participants = 0`. No drift toward neutral in MVP.
- **Worker crashes mid-run**: rollback; next invocation retries; unique keys absorb partial event writes.
- **Worker double-fired**: `FOR UPDATE` + partial-unique-index serialize them; loser sees `closed` and exits.
- **Conflicting inputs**: not possible — one choice per user; aggregation is order-independent.
- **Variable saturation**: `clamp(0, 100)` is a feature. Document.
- **Spam**: per-IP rate limit on user creation; per-user posting already capped to 1/cycle by schema.
- **Clock skew / DST**: everything UTC; worker exits early if invoked before `ends_at`.
- **Multi-day worker outage**: per §7, the worker loops in a transaction-per-cycle until the open cycle is in the future, naturally backfilling missed days. `closed_at` is pinned to nominal close time (not catch-up wall-clock), so history doesn't visibly skew. Decisions submitted *during* the outage all landed in the cycle that was open at submit time, so aggregation per cycle is still correct.
- **Anonymous users**: omitting `handle` on `POST /v1/users` creates a user with `handle = NULL`. There is no uniqueness check on NULL handles; many anonymous users may exist. Contribution endpoint still works since it keys on `user_id`.
- **Conflicting filters on `GET /v1/events`**: `cycle_id` and `limit` are mutually exclusive — supplying both → 400 `code: "conflicting_filters"`.
- **Determinism of aggregation**: §5 mandates `decimal` arithmetic and `MidpointRounding.AwayFromZero`. SQL-side aggregation is forbidden because integer division and float associativity would silently break determinism across re-runs and tests.

---

## 10. Acceptance criteria (definition of done)

### Functional — runnable end-to-end on a fresh clone

- [ ] `docker compose up -d && dotnet ef database update --project src/Driftworld.Data` brings up Postgres and creates the schema.
- [ ] Seed produces exactly the rows specified in §4 "Genesis seed": cycle 1 closed (genesis snapshot, all-50, participants=0), cycle 2 open, no other rows.
- [ ] `POST /v1/users` with a handle returns a `user_id` and the handle; second call with the same handle returns 409 ProblemDetails (`code: "duplicate_handle"`). Calls with `handle` omitted always succeed and return `handle: null`.
- [ ] `POST /v1/decisions` without `X-User-Id` returns 401 ProblemDetails (`code: "missing_user_id"`); with a malformed value returns 400 (`code: "malformed_user_id"`); with a parsable but unknown id returns 401 (`code: "unknown_user"`).
- [ ] `POST /v1/decisions` succeeds once per user per cycle; second attempt returns 409 ProblemDetails with `extensions.code="duplicate"` and `extensions.cycle_id` set.
- [ ] `GET /v1/world/current` returns the most recently closed cycle's state, the open cycle's metadata, and `active_events` computed by re-evaluating rules against that state per §6.
- [ ] `dotnet run --project src/Driftworld.Worker` after at least one decision: closes the cycle, writes a new `world_states` row matching the documented formula by hand-calc, opens a successor cycle.
- [ ] Decisions driving `economy` below 20 produce exactly one `recession` event row for that cycle; recovering `economy` to ≥ 20 in a later cycle removes `recession` from `active_events` without any new DB writes.
- [ ] `GET /v1/world/history?limit=N` returns the last N closed cycles, descending by `cycle_id`. `limit=0` and `limit=366` both return 400 ProblemDetails.
- [ ] `GET /v1/events?limit=201` returns 400 ProblemDetails.
- [ ] `GET /v1/users/{id}/contribution` matches hand-calc on a small fixture.
- [ ] Re-running the worker with no decisions and no time elapsed exits 0 with no row changes.
- [ ] If the open cycle's `ends_at` is in the past by 3 days, a single worker invocation closes 3 cycles in order and leaves the next one open.
- [ ] `GET /v1/events?cycle_id=42&limit=10` returns 400 ProblemDetails with `code: "conflicting_filters"`.
- [ ] `AggregateAndApply` unit test: a mean-delta of exactly +0.5 produces a +1 change in the variable (round half away from zero); a mean-delta of -0.5 produces -1.
- [ ] API host fails to start with a clear validation error if `appsettings.json` is missing `Driftworld:World:K`.

### Test coverage

- **Unit (`Driftworld.Core.Tests`)**: `AggregateAndApply` — empty cycle, all-build, mixed, saturation at 0/100; each rule at boundary.
- **Integration (`Driftworld.Api.Tests`, Testcontainers Postgres)**: each endpoint, happy + one error path; duplicate-decision returns 409.
- **Integration (`Driftworld.Worker.Tests`)**: full cycle (seed → decisions → close → asserts); double-invocation idempotency.

### Non-functional

- [ ] All success responses are `application/json`; all error responses are `application/problem+json` (RFC 7807) with the domain `code` in `extensions`.
- [ ] All timestamps ISO-8601 UTC with `Z`.
- [ ] All tunable world parameters (`K`, choice deltas, rule thresholds) live in `appsettings.json` under `Driftworld:World` and are bound via validated `IOptions<WorldOptions>`. No magic numbers in code.
- [ ] No secrets in repo; connection string via `appsettings.Development.json` + env-var override.
- [ ] README documents: prerequisites (.NET 10 SDK, Docker), `docker compose up`, `dotnet ef database update`, `dotnet run --project src/Driftworld.Api`, `dotnet run --project src/Driftworld.Worker`, `dotnet test`.

---

## 11. Local-dev quickstart (target shape, post-Phase-1)

```bash
# 1. Bring up Postgres
docker compose up -d

# 2. Apply schema
dotnet ef database update --project src/Driftworld.Data --startup-project src/Driftworld.Api

# 3. Run the API
dotnet run --project src/Driftworld.Api
# → http://localhost:5080  (or whatever launchSettings says)

# 4. (in another shell) advance the world
dotnet run --project src/Driftworld.Worker

# 5. Run all tests
dotnet test
```

No cloud account, no external services, no real cron required for development. The worker is invoked manually whenever you want to advance time — this is on purpose, because it makes the system testable on a laptop without waiting 24 hours.
