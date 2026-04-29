# Phase 1 — Local Skeleton & Data Model

## What You'll Learn
- Multi-project .NET solution layout — why we split `Api`, `Worker`, `Core`, `Data`
- EF Core 10 with Npgsql: code-first entities, the `dotnet ef` CLI, design-time vs runtime
- Postgres-specific schema features in EF: `uuid`, `smallint`, `jsonb`, identity columns, **partial unique indexes**
- The `IOptions<T>` pattern with **fail-fast validation** at startup
- Docker Compose for ephemeral Postgres — volumes, ports, healthchecks
- The seven specific pitfalls every .NET-on-Postgres project hits

By the end you'll be able to bring up an empty Driftworld DB on your laptop with one `docker compose up`, one `dotnet ef database update`, and confirm the genesis seed shape from §4 of the master plan.

> **Phase 1 ships no endpoints.** That's intentional. Decisions, the worker, and reads all hinge on a correct schema and a validated config. We get those right first, then build on top.

---

## 1. Concepts

### 1.1 Why four src projects?

ASP.NET monoliths often live in a single project. We split for one reason: **the cycle-close worker is a separate process from the API, but they share domain logic and data access**. The split lets us:

| Project              | Depends on                | Reason for existing                                          |
| -------------------- | ------------------------- | ------------------------------------------------------------ |
| `Driftworld.Core`    | nothing (no EF, no ASP)   | Pure domain: `WorldState`, `Choices`, `IRule`, `AggregateAndApply`. **Unit-testable without a DB.** |
| `Driftworld.Data`    | `Core`, EF Core, Npgsql   | `DriftworldDbContext`, entity classes, EF migrations         |
| `Driftworld.Api`     | `Core`, `Data`, ASP.NET   | Hosts the HTTP endpoints                                     |
| `Driftworld.Worker`  | `Core`, `Data`            | Console app for the cycle-close transaction                  |

The hard rule: **`Core` references nothing in your runtime.** No EF. No ASP.NET. No Npgsql. If you find yourself reaching for those, that thing belongs in `Data` or `Api` — not `Core`.

This isn't ceremony. It's the dependency direction that lets the same `AggregateAndApply` function be:
- unit-tested against in-memory inputs (Phase 3),
- reused in a "preview" endpoint later if we want one (post-MVP),
- and called by the worker against real DB rows.

### 1.2 EF Core 10 + Npgsql, mentally

EF Core sits between your C# objects and SQL. Npgsql is the Postgres driver underneath. The provider package `Npgsql.EntityFrameworkCore.PostgreSQL` is the glue.

When you call `dbContext.Cycles.Add(...)` and `SaveChanges()`, this happens:
1. EF tracks the entity in its change tracker.
2. On save, EF asks the **Npgsql provider** to translate `INSERT INTO cycles ...` into Postgres SQL.
3. Npgsql sends it over the wire using the Postgres binary protocol.

You will write **two** kinds of model code:
- **Entity classes** — POCOs with properties matching columns. Lives in `Driftworld.Data/Entities/`.
- **Fluent configuration** in `OnModelCreating` — the things conventions can't infer (partial unique indexes, jsonb columns, value converters, check constraints).

Don't fight EF's conventions. By default it will:
- Map a property called `Id` to a primary key.
- Pluralize class names for table names (`User` → `Users`). We'll override to lowercase snake_case.
- Use `int` identity for integer PKs.
- Map `Guid` to `uuid` automatically (Npgsql does this).
- Map `DateTime` to `timestamp without time zone` ⚠️ — see pitfall §3.1.

### 1.3 The `dotnet ef` workflow

`dotnet ef` is a global-or-local tool that introspects your code, compares it to a stored "snapshot", and emits SQL migrations. The two commands you'll use 95% of the time:

```bash
# After you change an entity or OnModelCreating:
dotnet ef migrations add <Name> \
  --project src/Driftworld.Data \
  --startup-project src/Driftworld.Api

# To apply pending migrations to the DB:
dotnet ef database update \
  --project src/Driftworld.Data \
  --startup-project src/Driftworld.Api
```

**Why two project flags?** EF needs to:
1. Know **where the migrations live** (`--project` → `Driftworld.Data`).
2. Know **how the app boots** so it can grab the configured `DbContext` (`--startup-project` → `Driftworld.Api`, because that's the project with the `Program.cs` that calls `AddDbContext` with the connection string).

Without `--startup-project`, EF won't find your connection string and you'll get a confusing "no DbContext found" error.

What gets generated:
- `Migrations/20260428_InitialCreate.cs` — the `Up()` and `Down()` SQL emitters.
- `Migrations/DriftworldDbContextModelSnapshot.cs` — EF's compare-against snapshot. Commit this.

### 1.4 `IOptions<T>` and validation

ASP.NET Core's "options pattern" reads config sections into typed objects and injects them via DI. The minimum:

```csharp
// in Program.cs
builder.Services
    .AddOptions<WorldOptions>()
    .Bind(builder.Configuration.GetSection("Driftworld:World"))
    .ValidateDataAnnotations()
    .Validate(o => o.K > 0, "K must be positive")
    .ValidateOnStart();   // ← critical: see pitfall §3.6
```

Anywhere you need it: `IOptions<WorldOptions>` in your constructor — the framework binds it.

For Driftworld we go a step further with **`IValidateOptions<WorldOptions>`** — a class that runs richer validation (checking that every choice's delta vector has all three variables, that rule names match `[a-z_]+`, etc.). This is the difference between the host failing at startup with a clear error vs. the worker crashing at first cycle close because someone shipped a typo'd `appsettings.json`.

### 1.5 Docker Compose for Postgres

Compose is overkill for one container, but it gives us:
- A reproducible `up`/`down` story across machines.
- Named volumes that persist across restarts but are easy to nuke (`docker compose down -v`).
- Healthchecks so your app can wait for "ready" rather than "started".
- A clean upgrade path when (post-MVP) you want to add Redis/Grafana/etc.

The shape we're aiming for:

```yaml
services:
  postgres:
    image: postgres:16
    ports:
      - "5433:5432"        # ← host:container; see pitfall §3.7
    environment:
      POSTGRES_USER: driftworld
      POSTGRES_PASSWORD: driftworld
      POSTGRES_DB: driftworld
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U driftworld -d driftworld"]
      interval: 5s
      retries: 10

volumes:
  pgdata:
```

**Why port 5433?** Because NCache (or a default postgres install) might already own 5432. We pick 5433 by convention so the two projects coexist on the same laptop.

---

## 2. How EF Core Maps Our Postgres Schema

The §4 data model has a few non-default features. Here's how each is expressed in `OnModelCreating`.

### 2.1 UUID primary keys
```csharp
modelBuilder.Entity<User>(e =>
{
    e.ToTable("users");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasColumnName("id");
    // No HasDefaultValueSql needed — we generate Guid in C#.
});
```
**Generate UUIDs in C# (`Guid.NewGuid()` / `Guid.CreateVersion7()`)**, not via Postgres `gen_random_uuid()`. Reason: keeps inserts independent of DB extensions and makes the IDs available *before* the round-trip — useful for logging and idempotency keys later.

### 2.2 Identity column on `cycles.id`
```csharp
modelBuilder.Entity<Cycle>(e =>
{
    e.ToTable("cycles");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id)
        .UseIdentityAlwaysColumn();   // GENERATED ALWAYS AS IDENTITY
});
```
`UseIdentityAlwaysColumn` is the strict variant. **It will reject explicit ID inserts** unless we use `OVERRIDING SYSTEM VALUE`. That bites us in the genesis seed (pitfall §3.2). Use `UseIdentityByDefaultColumn` instead if you want the lenient variant — but I recommend strict, because it forces the seed to be honest about what it's doing.

### 2.3 Partial unique index on `cycles(status) WHERE status='open'`
This is the linchpin of "exactly one cycle is open at any time." EF expresses it as:
```csharp
modelBuilder.Entity<Cycle>()
    .HasIndex(x => x.Status)
    .IsUnique()
    .HasDatabaseName("ix_one_open_cycle")
    .HasFilter("status = 'open'");
```
Two things to know:
- The `HasFilter` string is **raw SQL emitted into the migration** — it isn't parsed or validated. Get it right.
- This is a Postgres-specific feature. If you ever swap to SQL Server, the same filter syntax works there too; if you swap to SQLite, it won't.

### 2.4 `smallint` columns for world variables
```csharp
modelBuilder.Entity<WorldState>(e =>
{
    e.Property(x => x.Economy).HasColumnType("smallint");
    e.Property(x => x.Environment).HasColumnType("smallint");
    e.Property(x => x.Stability).HasColumnType("smallint");
});
```
The C# property type should be `short` (Int16) to match. **Don't make it `int` and hope the cast works** — the `smallint` column will accept it, but you've created an impedance mismatch where range-overflow bugs hide.

### 2.5 `jsonb` column on `events.payload`
```csharp
modelBuilder.Entity<Event>()
    .Property(x => x.Payload)
    .HasColumnType("jsonb");
```
The C# property is whatever you want — `string`, `JsonDocument`, or a strongly-typed record. Npgsql will round-trip all three. For Driftworld, **strongly-typed records bound via `System.Text.Json`** are the right call — the payloads are small and structured.

### 2.6 `timestamptz` everywhere
```csharp
e.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
```
**Required by Npgsql 6+:** the C# value must be a `DateTime` with `Kind = Utc` (or use `DateTimeOffset`). If you pass a `DateTime` with `Kind = Unspecified` you'll get a runtime exception. (See pitfall §3.1.)

### 2.7 Composite unique on `decisions(user_id, cycle_id)` and `events(cycle_id, type)`
```csharp
modelBuilder.Entity<Decision>()
    .HasIndex(x => new { x.UserId, x.CycleId })
    .IsUnique();

modelBuilder.Entity<Event>()
    .HasIndex(x => new { x.CycleId, x.Type })
    .IsUnique();
```
These are how the schema enforces "one decision per user per cycle" and "one event of each type per cycle." When EF tries to insert a duplicate, Npgsql throws `PostgresException` with `SqlState == "23505"`. Phase 2 maps that to `DuplicateDecisionException`.

---

## 3. Pitfalls (read this twice)

### 3.1 `DateTime` + `timestamptz` is a war zone in Npgsql 6+
The default behavior **rejects** `DateTime` values whose `Kind` is `Local` or `Unspecified`. You'll get:
> `InvalidCastException: Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'`

**Cures:**
- Always set `Kind = Utc` on `DateTime` values you save.
- Or use `DateTimeOffset` consistently.
- Or — last resort — flip the legacy compat switch: `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`. **Do not do this.** It papers over a real bug.

For Driftworld, **prefer `DateTime` with explicit UTC**. Every entity's `CreatedAt`, `StartsAt`, etc., is set to `DateTime.UtcNow` (or a `TimeProvider`-backed equivalent for testability). Document this rule in `Driftworld.Core` if you keep a clock abstraction.

### 3.2 Identity columns + the genesis seed
`UseIdentityAlwaysColumn` will reject:
```sql
INSERT INTO cycles (id, ...) VALUES (1, ...);
-- ERROR: cannot insert into column "id"
```
The seed needs cycle 1 (closed) to come **before** cycle 2 (open). Two paths:

1. Don't insert `id` explicitly. Insert cycle 1 first; identity gives it `1`. Then insert cycle 2; identity gives it `2`. **This is the right answer for Driftworld.** The seed never names IDs.
2. Override with `INSERT ... OVERRIDING SYSTEM VALUE`. Don't do this unless you're migrating data.

### 3.3 The seed's `T0` definition
"UTC midnight at-or-before seed time" sounds simple but the off-by-one bites. If you seed at 23:59:59 UTC on April 28, T0 = `2026-04-28 00:00:00Z`, cycle 2 ends at `2026-04-29 00:00:00Z` — **1 second from now**. The first time you run the worker it will close cycle 2 immediately. That's not wrong, but it's surprising. For dev convenience, the seed can take an optional `--cycle-start` to override T0.

### 3.4 `dotnet ef` design-time DbContext factory
For most projects, `--startup-project` is enough. But if your `Program.cs` builds an `IHost` with conditional logic (e.g., requires env vars to construct), `dotnet ef` may fail at design time with cryptic errors. The fix is an `IDesignTimeDbContextFactory<DriftworldDbContext>` in `Driftworld.Data`:

```csharp
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DriftworldDbContext>
{
    public DriftworldDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DriftworldDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=driftworld;Username=driftworld;Password=driftworld")
            .Options;
        return new DriftworldDbContext(options);
    }
}
```
**Heuristic:** add this if you hit design-time errors, not preemptively. It's a workaround, and the connection string in it will rot.

### 3.5 Migrations and the snapshot file
If two devs (or you across two machines) generate migrations on the same base, the snapshot files diverge and EF rejects the merge. We're solo so this won't happen — but **commit the snapshot every time you commit a migration**, and never edit it by hand.

### 3.6 Options validation runs lazily by default
This is the trap that most "fail-fast on misconfig" code has:
```csharp
// WRONG: validation runs the first time IOptions<WorldOptions> is requested.
builder.Services.AddOptions<WorldOptions>()
    .Bind(...)
    .Validate(o => o.K > 0, "K must be positive");
```
With this code, a misconfigured app will boot just fine. It crashes on the first request. **Always add `.ValidateOnStart()`** and ensure the host actually starts (which our minimal-API startup does). Then a missing `K` blows up `app.Run()`, not the first user.

### 3.7 Port 5432 vs 5433 silent confusion
Postgres inside the container always listens on 5432. We map it to host port 5433 in compose so we don't collide with NCache/system Postgres. **The connection string must use 5433** when running the app on the host:
```
Host=localhost;Port=5433;Database=driftworld;...
```
But if you ever `docker exec -it <container> psql`, you'll connect on 5432 — because you're inside the container. Two ports, same DB. This trips up first-time dockerized-Postgres users.

---

## 4. Code Layout (so you know what goes where)

```
Driftworld.Data/
├─ DriftworldDbContext.cs        # DbSet<>s + OnModelCreating (all the Fluent API lives here)
├─ Entities/
│  ├─ User.cs                    # POCOs only — no attributes if avoidable
│  ├─ Cycle.cs
│  ├─ Decision.cs
│  ├─ WorldState.cs
│  └─ Event.cs
├─ Migrations/
│  ├─ 20260428_InitialCreate.cs           # generated
│  └─ DriftworldDbContextModelSnapshot.cs # generated
└─ DesignTimeDbContextFactory.cs # add only if needed (pitfall §3.4)

Driftworld.Core/
├─ WorldOptions.cs               # the Driftworld:World config record
├─ WorldOptionsValidator.cs      # IValidateOptions<WorldOptions>
└─ Choices/                      # placeholder for Phase 3
   └─ (empty for now)

Driftworld.Api/
├─ Program.cs                    # AddDbContext, AddOptions, ValidateOnStart, app.Run()
├─ appsettings.json              # the default Driftworld:World values
└─ appsettings.Development.json  # connection string override

Driftworld.Worker/
└─ Program.cs                    # placeholder — real logic lands in Phase 3
```

Test projects mirror only their src project's name + `.Tests`. xUnit + FluentAssertions; Testcontainers added when integration tests start in Phase 2.

---

## 5. Definition of Done for Phase 1

This is the same checklist as the master plan §10, restated tactically:

- [ ] `docker compose up -d` brings Postgres up healthy (`docker compose ps` shows `healthy`).
- [ ] `dotnet ef database update --project src/Driftworld.Data --startup-project src/Driftworld.Api` runs cleanly against the dockerized Postgres.
- [ ] `psql -h localhost -p 5433 -U driftworld -d driftworld -c '\d'` shows all 5 tables.
- [ ] `psql ... -c '\di'` shows the partial unique index `ix_one_open_cycle` with a filter expression.
- [ ] Running the seed produces exactly the rows in §4 "Genesis seed (resolved)" — no more, no less.
- [ ] Running the seed twice is a no-op, not an error.
- [ ] Removing `Driftworld:World:K` from `appsettings.json` causes `dotnet run --project src/Driftworld.Api` to **fail at startup** with a message naming the missing config key.
- [ ] `dotnet build` and `dotnet test` (the empty-test-project run) both succeed.

When all these tick, Phase 1 is done. We then write `phase-2-users-and-decisions.md` and start on the API endpoints.

---

## 6. What we are deliberately NOT building in Phase 1

So you can resist scope creep:

- ❌ Any HTTP endpoint. Phase 2.
- ❌ The aggregation function (`AggregateAndApply`). Phase 3.
- ❌ Rule evaluation. Phase 4.
- ❌ Authentication of any kind beyond ad-hoc.
- ❌ Logging configuration beyond .NET defaults. Serilog comes in Phase 5.
- ❌ Connection pooling tuning. Defaults are fine for MVP.
- ❌ Tests beyond a single sanity test per project that proves the test runner works.

If you find yourself adding any of these to "save time later" — stop. Phase 1's value is in being unambiguously *done* before we move on.
