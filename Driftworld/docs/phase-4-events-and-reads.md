# Phase 4 — Events & Read Endpoints

## What You'll Learn

- **Pure rule evaluation** in `Driftworld.Core` — recursive evaluation of leaf + composite-`All` rules without coupling to the DB
- **Idempotent `events` writes** under partial-failure replay — `ON CONFLICT DO NOTHING` semantics in EF Core via `ExecuteSqlInterpolated` or constraint-violation swallow
- **Stateless "active events" semantics** — re-evaluating rules at read time and computing `since_cycle_id` by walking `world_states` backward
- **Pagination as 400, not silent clamp** — every `limit` outside its bounds returns ProblemDetails, never quietly truncated
- **Sharing a single evaluator** between the worker (write-time `events` rows) and the API (read-time `active_events`)
- **Contribution math** with deterministic tie-breaks — building a fixture, hand-calculating expected output, asserting both
- **`?cycle_id=` XOR `?limit=`** as a query-string contract — mutually exclusive parameters as 400 instead of priority rules

By the end you'll have four GET endpoints (`/v1/world/current`, `/v1/world/history`, `/v1/events`, `/v1/users/{id}/contribution`), the worker writing `events` rows during cycle close, and a comprehensive integration-test suite proving each behaves correctly under load.

> **Phase 4 ships no new write endpoints, no auth changes, no scheduling.** Phase 5 picks up rate limiting, Serilog, and the OS-level cron handoff.

---

## 1. Concepts

### 1.1 Rules — the shapes we already validate

The Phase 1 `WorldOptionsValidator` already enforces:

- Each rule is **either a leaf** (`Variable` + `Op` + `Threshold`) **or a composite** (`All: [...]` of sub-rules).
- Composite nesting is bounded at depth 3.
- Threshold values are in `[0, 100]`.

Phase 4 evaluates them. Two operations:

```csharp
public static IReadOnlyList<string> EvaluateMatching(WorldStateValue state, WorldOptions options);
//   returns the rule names whose body evaluates to true against `state`.

public static bool IsRuleHolding(WorldStateValue state, RuleOptions rule);
//   single-rule predicate, recursive for composites; used by the active-events backward walk.
```

Both live in `Driftworld.Core.Rules`. They take values, not entities — same shape as the Phase 3 aggregator. **No DbContext awareness, no allocation of state, fully unit-testable.**

### 1.2 Why config-driven rules (and not a hardcoded `IRule[]`)

The Phase 1.5 review (#7 / #18) pinned that rules live in `WorldOptions.Rules` with case-insensitive name lookup. Phase 4 honors that contract:

- `dotnet config edit` adds a `meltdown` rule → next cycle close evaluates it, writes events for it. No code change.
- Removing a rule from config means the next call to `EvaluateMatching` won't list it. **Existing event rows in the DB are not retroactively deleted** — they were correct at the time. The active-events read-time path *will* stop showing the rule (because it re-evaluates), so its absence from the UI is immediate.
- Renaming a rule in config breaks no DB invariant — old `events.type` values stay, new ones use the new name. A future migration or one-off `UPDATE events` can clean up if you care about historical normalization.

### 1.3 Idempotent `events` writes

The schema has `UNIQUE (cycle_id, type)` on `events`. The worker writes events as part of the same transaction as the cycle close — so you'd think we don't need ON CONFLICT, because a rolled-back transaction never persists.

**But** the cycle-close loop runs in *per-iteration transactions*. If a future change ever re-evaluates rules for an already-closed cycle (e.g., a backfill job, or someone running the worker twice on a cycle that closed but didn't write events due to a transient failure), the second write would hit the unique constraint and crash.

So we make the events write **idempotent at the SQL level**:

```csharp
await db.Database.ExecuteSqlInterpolatedAsync($@"
    INSERT INTO events (id, cycle_id, type, payload, created_at)
    VALUES ({Guid.NewGuid()}, {cycleId}, {ruleName}, {payload}::jsonb, {now})
    ON CONFLICT (cycle_id, type) DO NOTHING");
```

`ON CONFLICT DO NOTHING` is the canonical Postgres pattern — far cleaner than the EF round-trip alternative ("query first, insert if missing") which has TOCTOU windows.

### 1.4 Stateless active-events semantics

Per master plan §6, an event is **active** iff its rule still evaluates true against the most recently closed `world_state`. This is computed at read time, not stored.

Why stateless? Three reasons:

1. **No extra schema columns** — no `events.is_active` to keep in sync with state changes.
2. **Self-healing** — if you remove a rule from config, it stops being "active" instantly on the next read; no background sweep.
3. **Cheap** — one rule re-evaluation against the latest state, then a backward walk for each match.

#### `since_cycle_id` — the backward walk

For each rule that's currently active, we report the earliest contiguous closed cycle (walking backward from the latest closed state) where the rule was *also* active. This makes "recession since cycle 42" mean "recession started in cycle 42 and has been continuous since."

```
walk c = latest_closed_cycle_id, latest_closed_cycle_id - 1, ...:
    state_c = world_states.where(cycle_id == c)
    if (RuleEvaluator.IsRuleHolding(state_c, ruleOptions))
        since_cycle_id = c
    else
        break
```

The walk is bounded — by the history cap (365 cycles back) and by the rule first becoming false. In practice, both stop the loop quickly.

### 1.5 Pagination as 400, not silent clamp

Master plan §6 pins this:

| Endpoint | Default | Max |
|----------|---------|-----|
| `/v1/world/history?limit=N` | 30 | 365 |
| `/v1/events?limit=N` | 30 | 200 |

Out-of-range → 400 ProblemDetails with `code: "invalid_limit"`. **Not** silently clamped to max, **not** silently bumped to 1.

Why? Silent clamping is the kind of "helpful" behavior that hides client bugs. A client sending `limit=10000` thinks it gets 10000 rows; gets 365 silently; never sees a problem until production data investigation. A 400 makes the client stop and fix itself.

We extract this as a small helper:

```csharp
public static int ValidatedLimit(int? limit, int @default, int max) =>
    limit is null         ? @default
    : limit < 1            ? throw new InvalidLimitException(limit.Value, max, "must be ≥ 1")
    : limit > max          ? throw new InvalidLimitException(limit.Value, max, $"must be ≤ {max}")
    : limit.Value;
```

`InvalidLimitException` joins the existing domain-exception family (Phase 2). The `IExceptionHandler` already maps it to 400 ProblemDetails.

### 1.6 Contribution math

```
GET /v1/users/{id}/contribution
→ {
    "user_id": "...",
    "total_decisions": 17,
    "by_choice": { "build": 9, "preserve": 5, "stabilize": 3 },
    "alignment": { "with_majority_pct": 64 }
  }
```

`alignment.with_majority_pct` is the percentage of cycles in which:
- the user submitted a decision, AND
- the user's choice matches the modal (most-frequent) choice across all decisions in that cycle.

Implementation:

1. Pull all of the user's decisions, joined with each cycle's full decision set.
2. For each cycle the user participated in, compute the modal choice across that cycle's decisions.
3. Count cycles where `user.choice == modal_choice`.
4. Return `(matches / total) * 100`, integer percentage.

#### Tie-breaks

If two choices are tied for the modal in a cycle, pick the **alphabetically first**. Deterministic, simple, documented. The user's choice "matches the majority" if it equals the alphabetical winner. This makes ties a coin-flip (50/50 on which choice "matches"), which is the right MVP semantics — better than "no majority" (which special-cases the math) or skipping the cycle (which makes the denominator confusing).

### 1.7 Sharing the evaluator across worker and API

The worker calls `RuleEvaluator.EvaluateMatching` *inside* the cycle-close transaction, then writes events. The API calls the same function at read time for `active_events`. **Same code, two callsites.** This is the Phase 3 pattern (worker + API both use `WorldOptions`) extended to rules.

Why static (not an interface registered in DI)? Two reasons:

1. **No state.** Pure functions don't need a lifetime.
2. **No mockability requirement.** Unit tests against the real evaluator with crafted `WorldStateValue`s are the right tests for rule logic; mocking the evaluator would just test the wiring.

If we ever need rule evaluation to be pluggable (e.g., a remote rules engine), we can extract an interface then. **Don't preemptively abstract** (system prompt rule).

---

## 2. How Each Piece Looks

### 2.1 `RuleEvaluator`

```csharp
namespace Driftworld.Core.Rules;

public static class RuleEvaluator
{
    public static IReadOnlyList<string> EvaluateMatching(WorldStateValue state, WorldOptions options)
    {
        var matches = new List<string>();
        foreach (var (name, rule) in options.Rules)
        {
            if (IsRuleHolding(state, rule))
                matches.Add(name);
        }
        return matches;
    }

    public static bool IsRuleHolding(WorldStateValue state, RuleOptions rule)
    {
        if (rule.IsComposite)
            return rule.All!.All(sub => IsRuleHolding(state, sub));

        // Leaf — validator guaranteed Variable, Op, Threshold are non-null.
        var value = state.GetVariable(rule.Variable!.Value);
        return rule.Op!.Value switch
        {
            ComparisonOp.Lt  => value <  rule.Threshold!.Value,
            ComparisonOp.Lte => value <= rule.Threshold!.Value,
            ComparisonOp.Gt  => value >  rule.Threshold!.Value,
            ComparisonOp.Gte => value >= rule.Threshold!.Value,
            ComparisonOp.Eq  => value == rule.Threshold!.Value,
            _ => throw new InvalidOperationException($"Unknown op: {rule.Op}"),
        };
    }
}
```

`WorldStateValue.GetVariable(WorldVariable)` is a small switch returning `short`. Mirror of `ChoiceDelta.For`.

### 2.2 Events write inside `CycleCloser`

After computing `next` and adding the new `world_states` row, before the `Cycles.Add` for the successor:

```csharp
var matching = RuleEvaluator.EvaluateMatching(next, world);
foreach (var ruleName in matching)
{
    var payload = JsonSerializer.Serialize(new
    {
        economy = next.Economy,
        environment = next.Environment,
        stability = next.Stability,
    });

    await db.Database.ExecuteSqlInterpolatedAsync($@"
        INSERT INTO events (id, cycle_id, type, payload, created_at)
        VALUES ({Guid.NewGuid()}, {openCycle.Id}, {ruleName}, {payload}::jsonb, {closedAt})
        ON CONFLICT (cycle_id, type) DO NOTHING", ct);
}
```

Note: this runs inside the same transaction as the cycle close — the `ExecuteSqlInterpolatedAsync` call enlists in the ambient transaction automatically. If the close fails after, the events rollback too.

### 2.3 `GET /v1/world/current`

```csharp
public static async Task<IResult> GetCurrentAsync(
    DriftworldDbContext db,
    IOptions<WorldOptions> world,
    CancellationToken ct)
{
    var openCycle = await db.Cycles.FirstOrDefaultAsync(c => c.Status == CycleStatus.Open, ct)
        ?? throw new NoOpenCycleException();

    var latestState = await db.WorldStates
        .OrderByDescending(s => s.CycleId)
        .FirstAsync(ct);

    var stateValue = new WorldStateValue(
        latestState.Economy, latestState.Environment, latestState.Stability, latestState.Participants);

    var activeRules = RuleEvaluator.EvaluateMatching(stateValue, world.Value);
    var activeEvents = new List<ActiveEventDto>(activeRules.Count);

    foreach (var ruleName in activeRules)
    {
        var sinceCycleId = await ComputeSinceCycleIdAsync(
            db, world.Value.Rules[ruleName], latestState.CycleId, ct);
        activeEvents.Add(new ActiveEventDto(ruleName, sinceCycleId));
    }

    return Results.Ok(new CurrentWorldResponse(
        Cycle: new(openCycle.Id, openCycle.StartsAt, openCycle.EndsAt, "open"),
        State: new(stateValue.Economy, stateValue.Environment, stateValue.Stability, latestState.CycleId),
        ActiveEvents: activeEvents));
}
```

`ComputeSinceCycleIdAsync` is the backward-walk loop. It pages through `world_states` ordered descending by `cycle_id`, evaluates the rule against each, and returns the earliest contiguous match.

### 2.4 Pagination helper

```csharp
public static int Validate(int? limit, int defaultValue, int max)
{
    if (limit is null) return defaultValue;
    if (limit.Value < 1 || limit.Value > max)
        throw new InvalidLimitException(limit.Value, max);
    return limit.Value;
}
```

The endpoint signature uses nullable `int?` so an absent `limit` query string differs from `limit=0`.

---

## 3. Pitfalls (read this twice)

### 3.1 `ON CONFLICT DO NOTHING` doesn't return the inserted row

If you ever need the `id` of the just-written event row, switch to `RETURNING id` and check the result count. For Phase 4 we don't need it — the events are write-only from the worker's perspective.

### 3.2 `IsRuleHolding` recursion can stack-overflow on adversarial config

The validator caps composite nesting at depth 3. *If* you ever raise `MaxRuleDepth`, audit recursion. For Phase 4 we're fine.

### 3.3 Integer division surprise in alignment_with_majority_pct

```csharp
var pct = (matches / total) * 100;        // WRONG — int division → 0 or 1
var pct = (matches * 100) / total;        // RIGHT — integer percentage
var pct = (int)Math.Round((decimal)matches / total * 100m); // RIGHT — explicit
```

Use the multiply-first form or cast to decimal. The unit test should pin a fractional case (e.g., 1 of 3 = 33).

### 3.4 The `since_cycle_id` walk must order DESCENDING

If you `OrderBy(s => s.CycleId)` (ascending) and walk forward, you hit the genesis row first, which never matches threshold rules and you return immediately with the wrong answer. Always descending; walk backward; break on first non-match.

### 3.5 Walking past `cycle_id = 1` (genesis)

The genesis world_state row exists with all-50. For a `recession` rule (`economy < 20`), it never matches — the walk stops cleanly. But for a hypothetical `tepid` rule like `economy < 60`, the genesis state DOES match — and the walk would extend `since_cycle_id` all the way back to 1. That's correct! The world *has* been "tepid since cycle 1." Don't add a special case to skip genesis.

### 3.6 `Driftworld.Worker` needs `RuleEvaluator` available

It already references `Driftworld.Core` (Phase 3). Adding `RuleEvaluator` to `Driftworld.Core.Rules` doesn't change the dependency graph.

### 3.7 `cycle_id=` and `limit=` are mutually exclusive on `/v1/events`

Per master plan §6:
> `cycle_id` and `limit` are **mutually exclusive** — supplying both → `400` with `code: "conflicting_filters"`.

Validate at the endpoint signature, before the handler does any work. The endpoint accepts both as `int?`; the handler checks which is set:

```csharp
if (cycleId is not null && limit is not null)
    throw new ConflictingFiltersException("cycle_id", "limit");

if (cycleId is null && limit is null)
    limit = 30;  // default
```

`ConflictingFiltersException` is a new domain exception → 400 with `code: "conflicting_filters"`.

### 3.8 `active_events` has a hidden ordering contract

The order in the response should be **stable** so clients can rely on it. Two choices:
- Order by rule name alphabetically.
- Order by `since_cycle_id` ascending (oldest event first).

We pick **rule name alphabetical** — easier to reason about, doesn't change as cycles tick. Document.

### 3.9 EF Core's `OrderByDescending` over `cycle_id` index

The walk query repeats `db.WorldStates.Where(s => s.CycleId <= X).OrderByDescending(s => s.CycleId).FirstAsync()` per cycle in the worst case. Postgres uses the PK index for the order-by-desc; each query is O(log n). Worst-case for a 100-cycle-old recession is 100 round-trips at ~1ms each — 100ms. Acceptable for MVP. Post-MVP optimization: pull all states once, walk in C#. Don't preemptively optimize.

### 3.10 Contribution alignment — empty user

If a user has zero decisions, `total_decisions = 0`, `by_choice = {}`, `alignment.with_majority_pct = 0` (not divided-by-zero). Test this.

---

## 4. Code Layout

```
src/Driftworld.Core/
├─ Rules/
│  └─ RuleEvaluator.cs           # static, pure
├─ Aggregation/
│  └─ WorldStateValue.cs         # adds GetVariable(WorldVariable) helper
└─ Exceptions/
   ├─ InvalidLimitException.cs
   ├─ ConflictingFiltersException.cs
   └─ (existing)

src/Driftworld.Data/
├─ Cycles/
│  └─ CycleCloser.cs             # adds events-write block inside the txn
└─ (existing)

src/Driftworld.Api/
├─ Endpoints/
│  ├─ WorldEndpoints.cs          # GET /v1/world/current, /v1/world/history
│  ├─ EventsEndpoints.cs         # GET /v1/events
│  └─ ContributionEndpoints.cs   # GET /v1/users/{id}/contribution
├─ Pagination/
│  └─ LimitValidator.cs
└─ (existing)

tests/Driftworld.Core.Tests/
└─ RuleEvaluatorTests.cs

tests/Driftworld.Worker.Tests/
└─ CycleCloserTests.cs           # +cases for events writes

tests/Driftworld.Api.Tests/
├─ WorldEndpointsTests.cs
├─ EventsEndpointsTests.cs
└─ ContributionEndpointsTests.cs
```

---

## 5. Definition of Done

- [ ] `RuleEvaluator.EvaluateMatching` and `IsRuleHolding` exist in `Driftworld.Core.Rules` with ≥10 unit tests covering all four `ComparisonOp`s, leaf + composite, threshold boundaries, depth-3 nesting.
- [ ] `CycleCloser` writes events for every matching rule in the same transaction as the cycle close. Re-running on a cycle whose events are already written is a no-op (`ON CONFLICT DO NOTHING`).
- [ ] `GET /v1/world/current` returns latest closed state + open cycle metadata + `active_events` (re-evaluated at read time, ordered alphabetically by rule name, each with correct `since_cycle_id`).
- [ ] `GET /v1/world/history?limit=N`: default 30, max 365. `limit=0` and `limit=366` both return 400 ProblemDetails (`invalid_limit`).
- [ ] `GET /v1/events?cycle_id={int}` xor `?limit={int}`: default 30, max 200. Out-of-range → 400 `invalid_limit`. Both supplied → 400 `conflicting_filters`.
- [ ] `GET /v1/users/{id}/contribution` returns `total_decisions`, `by_choice`, `alignment.with_majority_pct` matching hand-calc on a small fixture.
- [ ] Live acceptance: drive `economy < 20` via decisions; run worker; `GET /v1/world/current` reports recession in `active_events` with `since_cycle_id` = the first cycle where economy dropped. Recover (decisions that bring economy back to ≥ 20); next cycle close + read drops recession from `active_events` *without* a new event row.
- [ ] All endpoint tests pass; full suite (Core + Data + Api + Worker) passes.

---

## 6. What We Are Deliberately NOT Building in Phase 4

- ❌ Real-time updates (SSE / WebSockets). Out of scope per master plan.
- ❌ Event payload schemas — payload is a flat `{ economy, environment, stability }` JSON object. Fine for MVP.
- ❌ A "previous active events" snapshot per cycle. Active is computed against latest state only.
- ❌ Aggregating contribution stats across all users (leaderboard). Single-user only.
- ❌ Cursor-based pagination. `?limit=N` is enough for MVP.
- ❌ Caching of `/v1/world/current`. Reads are cheap; revisit if measurements demand.
- ❌ Rate limiting, structured logs, OS-level cron. Phase 5.

---

## 7. After Phase 4

`phase-5-scheduling-and-polish.md` adds: per-IP rate limit on `POST /v1/users`, Serilog → console + rolling file, README docs for cron / Task Scheduler, final acceptance pass against the master plan §10. After Phase 5, the MVP is shippable per the original goal.
