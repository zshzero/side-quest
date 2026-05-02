# Phase 3 — Cycle-Close Worker

## What You'll Learn

- **Console-app `IHost`** — why the Worker uses the same DI primitives as the API
- **Pure aggregation function** — keeping `AggregateAndApply` in `Driftworld.Core` with zero DB awareness
- **`decimal` vs `double`** for deterministic rounding, and `MidpointRounding.AwayFromZero`
- **PostgreSQL row-level locking** (`SELECT … FOR UPDATE`) from EF Core via raw SQL
- **Per-iteration transaction loop** — how multi-day catch-up works *without* one giant transaction
- **`closed_at = LEAST(now(), ends_at + 5m)`** — why we pin nominal close time, not wall clock
- **Idempotency boundaries** — what makes the worker safe to run twice, ten times, or while another instance races
- **`TimeProvider` injection** for tests that simulate days/weeks elapsed

By the end you'll be able to advance the world by hand: `dotnet run --project src/Driftworld.Worker`. Decisions submitted to the API in cycle N produce a `world_states` row for cycle N when the worker runs after cycle N's `ends_at`.

> **Phase 3 ships no rule evaluation and no event-table writes.** Threshold events (`recession`, `golden_age`, etc.) and the read endpoints that surface them are Phase 4. This phase locks in the *math* and the *transactional state-machine* — getting decisions in cycle N → a world_states row in cycle N → a fresh open cycle N+1, repeatably.

---

## 1. Concepts

### 1.1 Why a separate console app, not an in-process scheduler

We chose this in §7 of the master plan, but it's worth re-grounding now that we're about to write the code:

- **API restarts don't disturb the schedule.** If the worker were `IHostedService` inside the API, every deploy resets the cron timer.
- **Independent logs and exit codes.** When the worker fails, an OS-level scheduler (cron / Task Scheduler) sees a non-zero exit code and can alert. An in-process worker buries failures in API logs.
- **Independent scaling.** You'd never want N API instances each firing the cycle-close — even if synchronization works (it does, via `SELECT … FOR UPDATE`), it's wasted compute and noisy logs.
- **Testability.** A console app is invoked manually for tests *and* for development — `dotnet run --project src/Driftworld.Worker` is the same binary as production cron fires.

### 1.2 Console-app `IHost`

ASP.NET Core uses `WebApplication.CreateBuilder(args)`. Console apps that want the same DI machinery use:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDriftworldData(builder.Configuration);
builder.Services.AddDriftworldOptions(builder.Configuration).ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);

using var host = builder.Build();
await host.StartAsync();   // runs validators, starts HostedServices (we have none)

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
// … run our work …

await host.StopAsync();
return 0;
```

Why this matters: **the same `AddDriftworldOptions` extension** registered in the API (Phase 2) is reused here. If a Phase 5+ change adjusts options validation, both hosts pick it up automatically. The Phase 1.5 review item #18 was specifically about preventing drift between these two `Program.cs` files; this is where that pays off.

### 1.3 The `AggregateAndApply` pure function

Lives in `Driftworld.Core/Aggregation/`. Signature:

```csharp
public static WorldStateValue AggregateAndApply(
    WorldStateValue prev,
    IReadOnlyList<string> choices,    // raw choice names from decisions in the cycle
    WorldOptions world);              // for K and Choice → delta map
```

Where `WorldStateValue` is a tiny POCO/record (`short Economy`, `short Environment`, `short Stability`, `int Participants`).

**The function knows nothing about the DB.** No `DbContext`, no `Decision` entity. It takes raw decision-choice strings, looks them up in `WorldOptions.Choices`, and returns a new `WorldStateValue`. This means:

- Unit tests are dirt-cheap (no Postgres).
- The orchestrator (in `Driftworld.Data`) does all the EF interaction; `Core` does the math.
- A future preview endpoint (post-MVP) could call the same function to show users "if the cycle ended now, the world would look like X."

### 1.4 `decimal` not `double`

Per the master plan §5:

```
mean_delta  = sum_delta_v / N              (decimal)
raw_v       = prev_v + K * mean_delta      (decimal)
new_v       = clamp(round_half_away_from_zero(raw_v), 0, 100)
```

Why `decimal`?

- **`double` is non-associative** for floating-point addition. `(a + b) + c != a + (b + c)` in general. If two implementations of the worker compute `mean_delta` over the same five choices in different orders, they can disagree at the LSB. Future you, debugging "why did the test fail in CI but pass locally," will hate past you.
- **`decimal` is exact** for the values we deal with (sums of small integers, division by small integers up to ~10⁶, multiplication by `K = 2`). No round-off, full reproducibility.
- **Cost:** `decimal` is software-implemented and ~10× slower than `double`. We do this math once per closed cycle, against ≤ thousands of decisions. The cost is irrelevant.

### 1.5 `MidpointRounding.AwayFromZero`

`Math.Round(0.5m)` defaults to banker's rounding (`MidpointRounding.ToEven`), which gives `0`. We don't want that — we want `+0.5` → `+1`, `-0.5` → `-1`. The "away from zero" mode is what humans expect and is what the master plan specifies:

```csharp
var rounded = (short)Math.Round(rawValue, MidpointRounding.AwayFromZero);
```

A unit test pins this — flip the rounding mode and the test breaks loudly.

### 1.6 `SELECT … FOR UPDATE` from EF Core

EF Core has no fluent API for `FOR UPDATE`. We drop to raw SQL inside the worker's transaction:

```csharp
var openCycle = await db.Cycles
    .FromSqlInterpolated($"SELECT * FROM cycles WHERE status = 'open' FOR UPDATE")
    .FirstOrDefaultAsync(ct);
```

This locks the row at row-level. Any other transaction that issues `SELECT … FOR UPDATE` against the same row blocks until our transaction commits or rolls back. Concurrent workers serialize cleanly — the loser sees `status='closed'` after the winner releases and bails out.

Why the partial unique index alone isn't enough: the partial unique index *prevents* two open cycles from existing, but it doesn't prevent two workers from both reading the open cycle, both deciding to close it, and one's INSERT of a successor failing on the unique constraint after both have written `world_states`. `FOR UPDATE` makes this serial.

### 1.7 Per-iteration transactions vs one big transaction

Master plan §7 says:

> Each loop iteration runs **in its own transaction**.

Why not wrap the whole multi-day catch-up loop in one transaction?

- **Lock duration.** A 3-day catch-up holds the cycle row locked for the entire close run. Other workers, manual `psql` queries, etc. all wait. Per-iteration commits release the lock between cycles.
- **Rollback blast radius.** If iteration 3 of 5 fails, one big transaction rolls back iteration 1 and 2's correct work. Per-iteration commits keep the work that succeeded.
- **Forward progress.** A transient failure in iteration 4 still leaves iterations 1–3 committed. Next worker invocation picks up from cycle 4.

The cost: the loop is *not* atomic. If the host is killed between iteration 2 and iteration 3, the world is in a state where cycles 1 and 2 are closed but cycle 3 is still "open with `ends_at` in the past." That's exactly the state the next worker run handles, by design — it's the same state we're treating as "missed cron run, catch up."

### 1.8 `closed_at = LEAST(now(), ends_at + interval '5 minutes')`

Surprising at first read. Master plan §7:

> `UPDATE cycles SET status='closed', closed_at=LEAST(now(), cycle.ends_at + interval '5 minutes')` — `closed_at` reflects nominal close time, not catch-up wall-clock, so backfills don't look like a 3-day-late close.

Concretely: if a cycle ended at `2026-04-01 00:00Z` and we're closing it now (`2026-04-04 12:00Z` because we missed 3 days of cron), naive `closed_at = now()` would record `2026-04-04 12:00Z` — which makes the historical record look like a 3-day-late close. That's misleading: the cycle's data was complete at the time it ended; we just didn't run the worker on time.

So we pin `closed_at` to the cycle's nominal close time + 5min slack (the same slack as our cron schedule). When the worker is on schedule, `now() ≈ ends_at + 5min` and `LEAST` picks `now()`. When the worker is days late, `LEAST` picks `ends_at + 5min` — the time it *would* have closed. History reads correctly.

### 1.9 Idempotency boundaries

The worker is idempotent at the cycle level: calling `RunAsync` repeatedly on the same DB state either advances the world by N cycles (if N cycles are overdue) or does nothing (if no cycle's `ends_at` is in the past).

Three properties make this safe:

1. **Existence check inside the transaction.** Every iteration starts with `SELECT … FROM cycles WHERE status='open' FOR UPDATE`. If `now() < ends_at`, exit. If no row returned, exit.
2. **Unique constraint on `world_states.cycle_id` (PK).** Re-inserting the same `cycle_id` fails loudly — but with the FOR UPDATE flow it can't happen, because once we close the cycle and commit, the next iteration sees a different open cycle.
3. **Partial unique index on `cycles.status='open'`.** Inserting a second "open" cycle fails — making it impossible to leave the world with two open cycles even on a partial-failure replay.

### 1.10 `TimeProvider` and test-time advancement

Phase 2's tests pinned a fixed clock for assertions. Phase 3 needs more: tests must simulate days passing. The pattern:

```csharp
private sealed class AdvanceableClock : TimeProvider
{
    private DateTimeOffset _now;
    public AdvanceableClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
```

Tests call `clock.Advance(TimeSpan.FromDays(3))`, then run the worker, then assert. Production uses `TimeProvider.System` (real wall clock).

---

## 2. How Each Piece Looks

### 2.1 `WorldStateValue`

```csharp
namespace Driftworld.Core.Aggregation;

public sealed record WorldStateValue(
    short Economy,
    short Environment,
    short Stability,
    int Participants);
```

A pure-value record. Translates trivially to/from the EF entity `WorldState`.

### 2.2 `AggregateAndApply`

```csharp
public static class WorldStateAggregator
{
    public static WorldStateValue AggregateAndApply(
        WorldStateValue prev,
        IReadOnlyList<string> choices,
        WorldOptions world)
    {
        if (choices.Count == 0)
            return prev with { Participants = 0 };

        var sums = new int[3]; // Economy, Environment, Stability
        foreach (var name in choices)
        {
            if (!world.Choices.TryGetValue(name, out var delta))
                throw new UnknownChoiceException(name, world.Choices.Keys.ToArray());
            sums[0] += delta.Economy;
            sums[1] += delta.Environment;
            sums[2] += delta.Stability;
        }

        var n = (decimal)choices.Count;
        var k = world.K;

        return new WorldStateValue(
            Economy:     ApplyOne(prev.Economy,     sums[0], n, k),
            Environment: ApplyOne(prev.Environment, sums[1], n, k),
            Stability:   ApplyOne(prev.Stability,   sums[2], n, k),
            Participants: choices.Count);
    }

    private static short ApplyOne(short prev, int sum, decimal n, decimal k)
    {
        var meanDelta = (decimal)sum / n;
        var raw = prev + k * meanDelta;
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp((int)rounded, 0, 100);
    }
}
```

Three things to notice:

- The function never sees `Cycle`, `Decision`, `WorldState` — only choice strings.
- `ApplyOne` is the inner kernel. Easy to spot-check by hand.
- The `UnknownChoiceException` throw is **defensive**. The API's `POST /v1/decisions` already rejects unknown choices, but if API and worker ever load a different `WorldOptions` (config drift between deploys), we want a loud failure instead of silent miscalculation.

### 2.3 `CycleCloser` — the orchestrator

Lives in `Driftworld.Data` (it touches the DbContext). Returns a result describing what it did:

```csharp
public sealed record CycleCloseResult(int CyclesClosed, IReadOnlyList<int> ClosedCycleIds);

public static class CycleCloser
{
    public static async Task<CycleCloseResult> RunAsync(
        DriftworldDbContext db,
        WorldOptions world,
        TimeProvider clock,
        CancellationToken ct = default)
    {
        var closed = new List<int>();
        while (await CloseOneIfDueAsync(db, world, clock, ct) is { } closedCycleId)
            closed.Add(closedCycleId);
        return new CycleCloseResult(closed.Count, closed);
    }

    // Returns the closed cycle id, or null if no cycle was due.
    private static async Task<int?> CloseOneIfDueAsync(
        DriftworldDbContext db, WorldOptions world, TimeProvider clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock the open cycle row.
        var openCycle = await db.Cycles
            .FromSqlInterpolated($"SELECT * FROM cycles WHERE status = 'open' FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (openCycle is null) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        if (now < openCycle.EndsAt)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        // Aggregate the cycle's decisions.
        var choices = await db.Decisions
            .Where(d => d.CycleId == openCycle.Id)
            .Select(d => d.Choice)
            .ToListAsync(ct);

        var prev = await db.WorldStates
            .OrderByDescending(s => s.CycleId)
            .Select(s => new WorldStateValue(s.Economy, s.Environment, s.Stability, s.Participants))
            .FirstAsync(ct);

        var next = WorldStateAggregator.AggregateAndApply(prev, choices, world);

        db.WorldStates.Add(new WorldState
        {
            CycleId = openCycle.Id,
            Economy = next.Economy,
            Environment = next.Environment,
            Stability = next.Stability,
            Participants = next.Participants,
            CreatedAt = ClampClosedAt(now, openCycle.EndsAt),
        });

        openCycle.Status = CycleStatus.Closed;
        openCycle.ClosedAt = ClampClosedAt(now, openCycle.EndsAt);

        db.Cycles.Add(new Cycle
        {
            StartsAt = openCycle.EndsAt,
            EndsAt = openCycle.EndsAt.AddHours(24),
            Status = CycleStatus.Open,
            ClosedAt = null,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return openCycle.Id;
    }

    private static DateTime ClampClosedAt(DateTime now, DateTime endsAt)
    {
        var nominal = endsAt.AddMinutes(5);
        return now < nominal ? now : nominal;
    }
}
```

Note: `ClampClosedAt` is the C#-side equivalent of `LEAST(now(), ends_at + interval '5 minutes')`. We do it in C# rather than via a SQL `UPDATE … LEAST(now(), …)` because EF doesn't translate `LEAST` natively and we already have the values in memory.

### 2.4 Worker `Program.cs`

```csharp
using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Cycles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDriftworldData(builder.Configuration);
builder.Services.AddDriftworldOptions(builder.Configuration).ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);

using var host = builder.Build();
await host.StartAsync();

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
var world = scope.ServiceProvider.GetRequiredService<IOptions<WorldOptions>>().Value;
var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

var result = await CycleCloser.RunAsync(db, world, clock);
Console.WriteLine($"Closed {result.CyclesClosed} cycle(s): [{string.Join(", ", result.ClosedCycleIds)}].");

await host.StopAsync();
return 0;
```

Top-level statements; same shape as the API's `Program.cs`. No `WebApplication` (no HTTP server), but the DI registrations are identical.

---

## 3. Pitfalls (read this twice)

### 3.1 `FOR UPDATE` requires an explicit transaction

Issuing `SELECT … FOR UPDATE` outside a transaction either no-ops the lock (auto-commit transaction releases it before you do anything useful) or errors depending on the driver. Always inside `BeginTransactionAsync`.

### 3.2 EF Core change tracker after `FromSqlInterpolated`

Entities returned by `FromSqlInterpolated` are tracked by default — modifying their properties (like setting `Status = Closed`) and calling `SaveChangesAsync` produces an UPDATE. Good. But if you write a query that returns a *projection* (anonymous type), nothing is tracked and `db.Cycles.Update(...)` is required. Stick to entity-returning raw SQL when you mean to mutate.

### 3.3 `await using var tx` inside `while`

```csharp
while (...)
{
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    // ...
    await tx.CommitAsync(ct);
}  // tx is disposed at end of EACH iteration — correct
```

The `await using` inside the loop body scopes per-iteration, which is what we want. Don't refactor it out of the loop for "cleanness" — you'd accidentally make one giant transaction.

### 3.4 Empty cycles must not crash

`AggregateAndApply` with an empty `choices` list returns `prev with { Participants = 0 }`. The orchestrator's query `db.Decisions.Where(d => d.CycleId == ...).Select(d => d.Choice).ToListAsync()` returns `List<string>()` — perfectly fine. Test this case explicitly.

### 3.5 `decimal` cast subtleties

`(decimal)sum / n` works because both operands convert to decimal. `sum / n` (where `n` is `decimal`) also works. `(decimal)(sum / n)` does **integer division first**, then casts. Order matters; the unit tests catch this.

### 3.6 `Math.Round(decimal, MidpointRounding)` is the overload you want

There's `Math.Round(double, MidpointRounding)` too. Don't accidentally pass a `double` (e.g., from `(double)raw`). It silently changes the math, and unit tests on small inputs may not catch it.

### 3.7 The first-cycle prev-state lookup

`OrderByDescending(s => s.CycleId).FirstAsync()` is the prev-state query. This works because the genesis seed always inserts a `world_states` row keyed to cycle 1. If you ever delete that seed row (you shouldn't), the query throws — that's the right behavior, the world has no previous state and the worker can't proceed.

### 3.8 Time injection in the worker host

`TimeProvider.System` reads the wall clock. For tests, a custom `AdvanceableClock` is registered as a singleton. Phase 2's tests already do this for the API; Phase 3 just extends the pattern to the worker.

### 3.9 The worker shouldn't take `DbContext` from a singleton

`DbContext` is scoped, not singleton. The Worker `Program.cs` creates a scope explicitly:

```csharp
using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
```

Forgetting the scope works in tests but throws `InvalidOperationException` in production with `ServiceLifetime.Scoped` violations (depending on EF Core version). Always scope.

### 3.10 `WorldOptions.Choices` lookup is case-insensitive — so what comes IN matters less than what goes OUT

Our API rejects unknown choices but stores whatever case the client sent. The worker's `world.Choices.TryGetValue(name, out var delta)` is case-insensitive (Phase 1.5 #7), so `"BUILD"` matches `"build"` in config. Works correctly. But if a future config edit removes a choice that's already in past `decisions` rows, the worker throws `UnknownChoiceException` for those rows. There's no auto-recovery — you have to either restore the choice in config or delete the offending decisions. Document this for ops.

---

## 4. Code Layout

```
src/Driftworld.Core/
├─ Aggregation/
│  ├─ WorldStateValue.cs              # immutable value type
│  └─ WorldStateAggregator.cs         # the AggregateAndApply pure function
└─ (existing files unchanged)

src/Driftworld.Data/
├─ Cycles/
│  ├─ CycleCloser.cs                  # orchestrator with FOR UPDATE + per-iteration txn
│  └─ CycleCloseResult.cs
└─ (existing files unchanged)

src/Driftworld.Worker/
└─ Program.cs                         # IHost wiring; calls CycleCloser.RunAsync

tests/Driftworld.Core.Tests/
└─ WorldStateAggregatorTests.cs       # pure unit, 10+ cases incl. rounding boundaries

tests/Driftworld.Worker.Tests/
├─ WorkerPostgresFixture.cs           # Testcontainers + advanceable clock
├─ CycleCloserTests.cs                # full integration tests
└─ (replaces empty UnitTest1)
```

---

## 5. Definition of Done (Phase 3)

- [ ] `WorldStateAggregator.AggregateAndApply` exists in `Driftworld.Core` and is fully unit-tested (≥10 cases including 3 rounding-boundary cases and 2 saturation cases).
- [ ] `CycleCloser.RunAsync` exists in `Driftworld.Data` and:
  - [ ] Uses `SELECT … FOR UPDATE` to lock the open cycle.
  - [ ] Per-iteration transaction loop (not one big transaction).
  - [ ] `closed_at = LEAST(now(), ends_at + 5 minutes)` semantics.
  - [ ] No-ops if `now() < ends_at`.
- [ ] `Driftworld.Worker/Program.cs` is a runnable console app: `dotnet run --project src/Driftworld.Worker`.
- [ ] Worker uses `AddDriftworldOptions` + `AddDriftworldData` (same DI as API).
- [ ] Integration test: seed → 5 hand-picked decisions → run worker → assert exact `world_states` row by hand-calc → assert successor cycle is open → second run is a no-op.
- [ ] Integration test: back-date the open cycle's `ends_at` 3 days into the past → run worker once → 3 cycles closed in order, all `world_states` rows correct, exactly one open cycle remains in the future.
- [ ] Integration test: concurrent workers (5 tasks racing) close exactly the cycles that are due, never double-close.
- [ ] README documents the worker invocation and notes that re-running is safe.

---

## 6. What We Are Deliberately NOT Building in Phase 3

- ❌ Rule evaluation (`IRule`, the four rules from `WorldOptions.Rules`). Phase 4.
- ❌ `events` table writes. Phase 4.
- ❌ `GET /v1/world/current`, `GET /v1/world/history`, `GET /v1/events`, `GET /v1/users/{id}/contribution`. Phase 4.
- ❌ Real cron / Task Scheduler wiring. Phase 5.
- ❌ Logging beyond .NET defaults. Phase 5.
- ❌ Worker metrics or telemetry.

---

## 7. After Phase 3

`phase-4-events-and-reads.md` will pick up where we stop, adding rule evaluation to the worker (re-using its existing transaction) and shipping the four GET endpoints with their pagination, "active events" semantics, and contribution math.
