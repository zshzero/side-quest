# Phase 2 — Users & Decisions Endpoints

## What You'll Learn

- **Minimal-API endpoint groups** (`MapGroup`) and DTOs as records
- **`X-User-Id` lightweight auth**: how to distinguish "missing", "malformed", "unknown", and why each gets a different status code
- **ProblemDetails (RFC 7807)** — the structure, when to use `extensions`, and how ASP.NET Core 10 emits it
- **`IExceptionHandler`** — the .NET 8+ pattern that supersedes custom middleware for exception-to-response mapping
- **Domain exceptions in `Driftworld.Core`** — why exceptions belong to the domain layer, not the API layer
- **Mapping `PostgresException.SqlState == "23505"`** to a 409 — the canonical "DB enforces invariant, code translates to API contract" pattern
- **FluentValidation** as a *concept*; we'll use it surgically (one place), not everywhere
- **Integration testing the API end-to-end** — `WebApplicationFactory<Program>` + Testcontainers Postgres in one shared fixture

By the end you'll have two real endpoints (`POST /v1/users`, `POST /v1/decisions`), correct ProblemDetails error shapes for every failure mode, and integration tests that prove each one against a real Postgres.

> **Phase 2 ships no aggregation logic and no read endpoints.** The aggregation function belongs in Phase 3 (worker), and read endpoints (`GET /v1/world/current` etc.) belong in Phase 4. We focus this phase entirely on the *write path* — getting decisions reliably into the DB.

---

## 1. Concepts

### 1.1 Why endpoint groups (`MapGroup`)

A minimal-API host can scale from `app.MapGet("/", ...)` to dozens of endpoints, but the file gets unwieldy fast. **Endpoint groups** let you cluster related endpoints, share a common prefix, and apply filters to all of them at once.

Sketch:
```csharp
var v1 = app.MapGroup("/v1");
var users = v1.MapGroup("/users");
users.MapPost("/", CreateUser);
users.MapGet("/{id}", GetUser);
```

Two reasons we want this *now* even with only two endpoints:
1. **`/v1` is a long-lived prefix.** When Phase 4 adds more endpoints, they go on the same group; the version prefix never gets re-typed.
2. **Filters apply to the group.** Phase 5 will add per-IP rate limiting on `POST /v1/users` only — easier to express as `users.AddRateLimiter()` than as a per-endpoint annotation.

### 1.2 DTOs as records, in the endpoint file

Minimal APIs in .NET 10 have great support for `record` types as both inputs and outputs:

```csharp
public sealed record CreateUserRequest(string? Handle);
public sealed record CreateUserResponse(Guid UserId, string? Handle);
```

Records are immutable, value-equality-friendly (good for tests), and minimal-API model binding handles them out of the box for JSON. Don't use entity classes (`User`, `Decision`) as DTOs — the API contract should be *separate* from the persistence shape so adding a column to `users` doesn't accidentally become a public API change.

DTOs live alongside the endpoint file (`Endpoints/UsersEndpoints.cs`) for now. If they ever need to be shared across the API and the worker, they get extracted to a separate `Driftworld.Contracts` project.

### 1.3 `X-User-Id` — three failure modes, three status codes

Per master plan §6, `POST /v1/decisions` requires the `X-User-Id` header. Three distinct failure shapes:

| Condition                        | Status | `extensions.code`    |
| -------------------------------- | ------ | -------------------- |
| Header absent                    | 401    | `missing_user_id`    |
| Header present but not a UUID    | 400    | `malformed_user_id`  |
| Header present, parseable, but `users` row not found | 401 | `unknown_user` |

Why distinguish? Because the *client's correct response* is different in each case:
- Missing → "the client forgot to send it" → fix the client code.
- Malformed → "the client sent garbage" → 400 (client-side bug, not auth).
- Unknown → "the user existed once but no longer does, or never existed" → re-register the user.

Lumping all three into 401 makes debugging client integrations harder. Each gets a unique `extensions.code` so client error-handling can branch on it.

### 1.4 ProblemDetails — anatomy

RFC 7807 says all error responses should be `application/problem+json` with a standard shape:

```json
{
  "type":     "https://driftworld/errors/duplicate-decision",
  "title":    "Decision already submitted for this cycle",
  "status":   409,
  "detail":   "User a1.. already decided in cycle 43.",
  "instance": "/v1/decisions",

  "code":     "duplicate",
  "cycle_id": 43
}
```

The standard fields are `type`, `title`, `status`, `detail`, `instance`. **Anything else is an "extension"** — additional data the standard tolerates without specifying. We put our domain `code` and any context (`cycle_id`, `user_id`) here.

Why not just `{ "code", "message" }`? Because:
- Standard tooling (browsers, log aggregators, OpenAPI generators) understands `application/problem+json`.
- The `type` URI gives clients a stable identifier even if the human-readable `title` is translated or rewritten.
- ASP.NET Core 10 emits ProblemDetails *for free* on most error paths once you call `AddProblemDetails()`; you mostly just have to not fight it.

### 1.5 `IExceptionHandler` — the modern way

Pre-.NET 8, you'd write custom middleware: a class with `InvokeAsync(HttpContext, RequestDelegate)` that wraps `try/catch` around the next call and translates exceptions to responses. Works, but verbose.

.NET 8 introduced `IExceptionHandler`:

```csharp
public sealed class DriftworldExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception ex, CancellationToken ct)
    {
        // return true if you handled it (write the response)
        // return false to let the next handler / framework take over
    }
}
```

Registration is one-liner: `services.AddExceptionHandler<DriftworldExceptionHandler>()` plus `app.UseExceptionHandler()`. Multiple handlers are chained — the first that returns `true` wins.

The pattern: one handler matches your **domain exceptions** explicitly (returns 4xx ProblemDetails); a fallback either emits a generic 500 or returns `false` (lets ASP.NET emit the default 500 ProblemDetails).

### 1.6 Domain exceptions in `Driftworld.Core`

`DuplicateDecisionException`, `UnknownChoiceException`, `NoOpenCycleException`, etc. live in `Driftworld.Core`. Two reasons:

1. **The Worker (Phase 3) raises some of them too.** `NoOpenCycleException` would fire from the cycle-close worker if it found an inconsistency. Both API and worker share `Driftworld.Core`; neither reaches into the API project.
2. **Translation is one-way.** `Core` throws abstract domain errors. The API (`IExceptionHandler`) and the Worker (its own catch block + logging) translate them into their respective output formats. Domain code stays unaware of HTTP, ProblemDetails, status codes.

Each domain exception carries a `Code` (the domain identifier — `"duplicate"`, `"missing_user_id"`) and any structured context fields. The `IExceptionHandler` reads these, no reflection or string parsing needed.

### 1.7 `PostgresException.SqlState == "23505"` — the unique-violation translation

We *want* the database to enforce "one decision per user per cycle" — that's what the `UNIQUE (user_id, cycle_id)` index does. We don't pre-check by querying first (it's a TOCTOU race). Instead:

```csharp
try
{
    db.Decisions.Add(new Decision { … });
    await db.SaveChangesAsync(ct);
}
catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } pg)
{
    // pg.ConstraintName tells you WHICH unique constraint
    // ux_decisions_user_cycle vs ix_users_handle vs ux_events_cycle_type
    throw new DuplicateDecisionException(userId, cycleId);
}
```

`23505` is the SQLSTATE code for "unique_violation" — same in PG, MySQL, SQL Server (as 2627), Oracle. Catching on `SqlState` makes the code DB-vendor-independent (modulo the wrapping exception type).

**Critical**: distinguish *which* unique constraint failed via `pg.ConstraintName`. If `POST /v1/users` and `POST /v1/decisions` share an exception handler that just maps `23505` → 409, a bug in users would surface as a "duplicate decision" error. Always branch on the constraint name.

### 1.8 FluentValidation — surgical use

FluentValidation is a fluent-API validation library:
```csharp
public class HandleValidator : AbstractValidator<string>
{
    public HandleValidator()
    {
        RuleFor(h => h).Length(3, 32).Matches("^[a-zA-Z0-9_-]+$");
    }
}
```

It shines when:
- You have many fields with conditional rules
- You want validation logic separable from the endpoint
- You need composable validators

For Phase 2 we have **two fields total** that need validation: `handle` (when present) and `choice` (membership in a config-driven set). FluentValidation is overkill for the latter. We'll use it for `handle` to **demonstrate the pattern**, and validate `choice` inline against `WorldOptions.Choices.ContainsKey(input)`.

If Phase 2's validation grows, we can extract everything into validators later. Don't introduce the library *everywhere* before there's a reason — see the system prompt's "no premature abstractions" rule.

### 1.9 Integration testing — `WebApplicationFactory` + Testcontainers in one fixture

Phase 1 had:
- `Driftworld.Api.Tests` — `WebApplicationFactory<Program>` only (no DB needed for the smoke tests).
- `Driftworld.Data.Tests` — Testcontainers Postgres only (no API needed).

Phase 2 needs both: spin up Postgres, point the API host at it, exercise endpoints over HTTP, assert ProblemDetails shapes. The pattern:

```csharp
public sealed class ApiPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")...

    public WebApplicationFactory<Program> CreateFactory(/* with config override */) {…}
    public async Task ResetAsync() {…}        // truncate between tests
    public async Task SeedGenesisAsync() {…}  // call GenesisSeeder
}
```

A single `[CollectionDefinition]` shares it across all API integration tests so we pay the container startup cost once per test run, not once per test.

---

## 2. How ASP.NET Core 10 Does Each Piece

### 2.1 Endpoint group with handler reference

```csharp
var v1 = app.MapGroup("/v1");

var users = v1.MapGroup("/users");
users.MapPost("/", UsersEndpoints.CreateUserAsync)
     .WithName("CreateUser");
```

Handler is a static method that takes the bound types and returns `IResult`:

```csharp
public static async Task<IResult> CreateUserAsync(
    CreateUserRequest body,
    DriftworldDbContext db,
    IValidator<string?> handleValidator,
    TimeProvider clock,
    CancellationToken ct)
{
    // …validate, insert, return Results.Created(...)
}
```

Minimal APIs do DI for handler parameters from the request services. `CreateUserRequest` comes from the JSON body; the rest are injected.

### 2.2 ProblemDetails wiring

```csharp
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
        // Could also stamp a trace id, etc.
    };
});

builder.Services.AddExceptionHandler<DriftworldExceptionHandler>();

// ...
app.UseExceptionHandler();   // before MapXxx, before app.Run()
```

`AddProblemDetails()` does two things: registers an `IProblemDetailsService` that knows how to serialize `ProblemDetails` correctly, *and* wires automatic ProblemDetails emission for status-only responses (e.g. `Results.NotFound()` returns ProblemDetails JSON instead of an empty body).

### 2.3 Custom `IExceptionHandler`

```csharp
public sealed class DriftworldExceptionHandler(IProblemDetailsService problemDetails)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception ex, CancellationToken ct)
    {
        var (status, problem) = ex switch
        {
            DuplicateDecisionException e =>
                (StatusCodes.Status409Conflict, BuildProblem(e, "Decision already submitted for this cycle")),
            UnknownUserException e =>
                (StatusCodes.Status401Unauthorized, BuildProblem(e, "Unknown user")),
            // …
            DriftworldException e => (e.HttpStatus, BuildProblem(e, e.Title)),
            _ => default
        };

        if (problem is null) return false; // not ours; let the next handler / default 500 kick in

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
```

Two things to notice:
1. **Returning `false`** for unknown exceptions lets ASP.NET's default exception handler emit a generic 500 ProblemDetails. We don't have to handle "everything else" ourselves.
2. **`IProblemDetailsService.TryWriteAsync`** does the right thing automatically: sets `Content-Type: application/problem+json`, applies `CustomizeProblemDetails`, serializes.

### 2.4 The handler reading `X-User-Id`

```csharp
public static async Task<IResult> CreateDecisionAsync(
    CreateDecisionRequest body,
    HttpContext http,
    DriftworldDbContext db,
    IOptions<WorldOptions> world,
    TimeProvider clock,
    CancellationToken ct)
{
    if (!http.Request.Headers.TryGetValue("X-User-Id", out var raw) || raw.Count == 0)
        throw new MissingUserIdException();

    if (!Guid.TryParse(raw.ToString(), out var userId))
        throw new MalformedUserIdException(raw.ToString());

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw new UnknownUserException(userId);
    // …
}
```

We *throw*, we don't return error results. The `IExceptionHandler` translates the exception into the right ProblemDetails. Endpoint code stays linear and readable.

### 2.5 Handler test asserting ProblemDetails

```csharp
[Fact]
public async Task Duplicate_decision_returns_409_problemdetails()
{
    var client = _fx.CreateClient();
    // … seed user + open cycle, post once, post again

    var response = await client.PostAsJsonAsync("/v1/decisions",
        new { choice = "build" },
        new HttpClientOptions { Headers = { ["X-User-Id"] = userId.ToString() } });

    response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

    var doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    doc.GetProperty("status").GetInt32().Should().Be(409);
    doc.GetProperty("code").GetString().Should().Be("duplicate");
    doc.GetProperty("cycle_id").GetInt32().Should().Be(/* expected */);
}
```

---

## 3. Pitfalls (read this twice)

### 3.1 `app.UseExceptionHandler()` is order-sensitive

Must be called **before** `app.MapGroup(...)` / `app.MapGet(...)`. Otherwise endpoint exceptions bypass the handler. Symptom: tests pass for the happy path but error-path tests get plain 500s with HTML bodies.

### 3.2 `EmptyDiagnosticSource` swallows your exception in dev

If you're running with the developer exception page middleware on (default in `Development`), it intercepts exceptions *before* `UseExceptionHandler`. Symptom: the API returns the dev exception page in tests instead of ProblemDetails. Two fixes:
- Use `UseEnvironment("Test")` in `WebApplicationFactory` (which we already do) — non-Development envs skip the dev page.
- Or: wrap `app.UseDeveloperExceptionPage()` in `if (env.IsDevelopment())` and rely on `UseExceptionHandler` for everything else.

### 3.3 `DbUpdateException.InnerException` may not be `PostgresException`

It usually is, but other DB providers wrap differently. If you ever target both Postgres and SQL Server (we don't), pattern-match defensively:
```csharp
catch (DbUpdateException e) when (e.InnerException is PostgresException pg && pg.SqlState == "23505")
```
The `is X x &&` form short-circuits cleanly.

### 3.4 Constraint-name discrimination is essential

`POST /v1/users` and `POST /v1/decisions` both throw `DbUpdateException` with `SqlState=23505` on duplicate, but they should produce different ProblemDetails (`duplicate_handle` vs `duplicate`). **Always branch on `pg.ConstraintName`** — never assume which constraint fired:

```csharp
catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } pg)
{
    throw pg.ConstraintName switch
    {
        "ix_users_handle"          => new DuplicateHandleException(handle),
        "ux_decisions_user_cycle"  => new DuplicateDecisionException(userId, cycleId),
        _                          => new DriftworldException("unknown_unique_violation", "Unexpected unique constraint violation"),
    };
}
```

### 3.5 `Results.Problem(...)` vs throwing — pick one

ASP.NET Core has both:
- `return Results.Problem(statusCode: 409, …)` — explicit, returns IResult.
- `throw new DuplicateDecisionException(...)` — handled by `IExceptionHandler`.

Mixing them in the same codebase confuses readers and makes the IExceptionHandler easy to bypass accidentally. **For Phase 2 we throw exclusively.** The IExceptionHandler is the single source of truth for the error→response mapping.

### 3.6 The `WebApplicationFactory<Program>` test doesn't see startup-time `ConfigureWebHost` overrides

Top-level statements in `Program.cs` execute *before* `WebApplicationFactory`'s `ConfigureWebHost` overrides apply. Phase 1 hit this — solved by reading the connection string lazily inside `AddDbContext`'s lambda. Same rule applies anywhere else you read configuration: do it inside DI lambdas, not at the top of `Program.cs`.

### 3.7 Testcontainers per-test vs per-fixture

If you spin up a fresh Postgres per test, your suite goes from 200ms to 30s. **Use a shared collection fixture**. Reset state with `TRUNCATE … RESTART IDENTITY CASCADE` between tests — fast, correct, and resets identity sequences so `cycles.id` starts at 1 in every test (predictable assertions).

### 3.8 `last_seen_at` updates inside the same transaction as the decision insert

`POST /v1/decisions` should bump `users.last_seen_at`. Doing this in a separate `SaveChangesAsync` after the decision insert means: if the decision insert succeeds and the `last_seen_at` update fails, you have an inconsistent state. Combine them:

```csharp
user.LastSeenAt = clock.GetUtcNow().UtcDateTime;
db.Decisions.Add(decision);
await db.SaveChangesAsync(ct);   // both go in one transaction
```

EF Core groups all pending changes into one transaction by default — this is "the right thing" provided you make both modifications before a single `SaveChangesAsync` call.

### 3.9 `IOptions<WorldOptions>.Value.Choices.ContainsKey(...)` is case-insensitive (Phase 1.5 fix #7)

We already initialized the dictionary with `StringComparer.OrdinalIgnoreCase`. So `"Build"` and `"build"` both match. Phase 2 endpoints *don't need to lower-case input themselves*. Add a unit test that proves this — quietly regressing the comparer would be hard to spot.

### 3.10 A 401 without a `WWW-Authenticate` header is technically wrong per RFC 7235

We're returning 401 for `missing_user_id` and `unknown_user` without a `WWW-Authenticate` header (we have no challenge scheme). Strict HTTP linters complain. For MVP scope, we accept this; document in the response that this is a domain-401, not a standard auth-401. If a future phase adds real auth, the handler should set the header.

---

## 4. Code Layout

```
src/Driftworld.Core/
├─ Exceptions/
│  ├─ DriftworldException.cs        # base — Code, HttpStatus, optional context dict
│  ├─ DuplicateDecisionException.cs
│  ├─ DuplicateHandleException.cs
│  ├─ UnknownChoiceException.cs
│  ├─ UnknownUserException.cs
│  ├─ MissingUserIdException.cs
│  ├─ MalformedUserIdException.cs
│  └─ NoOpenCycleException.cs
└─ (existing types unchanged)

src/Driftworld.Api/
├─ Endpoints/
│  ├─ UsersEndpoints.cs             # /v1/users routes + DTOs
│  └─ DecisionsEndpoints.cs         # /v1/decisions routes + DTOs
├─ Validation/
│  └─ HandleValidator.cs            # FluentValidation rule for handle
├─ ErrorHandling/
│  └─ DriftworldExceptionHandler.cs # IExceptionHandler — maps domain exceptions
├─ ServiceCollectionExtensions.cs   # AddDriftworldApi() — pulls in ProblemDetails, validators, handler
├─ Program.cs                       # uses AddDriftworldApi(), AddExceptionHandler, MapGroup
└─ (existing files unchanged)

tests/Driftworld.Api.Tests/
├─ ApiPostgresFixture.cs            # WebApplicationFactory<Program> + Testcontainers
├─ UsersEndpointsTests.cs
└─ DecisionsEndpointsTests.cs
```

---

## 5. Definition of Done (Phase 2)

- [ ] `POST /v1/users` with `{ "handle": "ada" }` returns 201 + `{ user_id, handle: "ada" }`.
- [ ] `POST /v1/users` with `{}` (anonymous) returns 201 + `{ user_id, handle: null }`.
- [ ] `POST /v1/users` with a duplicate handle returns 409 `application/problem+json` with `extensions.code = "duplicate_handle"`.
- [ ] `POST /v1/users` with a malformed handle (e.g. `"ab"` or `"with-spaces "`) returns 400 ProblemDetails with `extensions.code = "invalid_handle"`.
- [ ] `POST /v1/decisions` without `X-User-Id` returns 401 ProblemDetails (`missing_user_id`).
- [ ] `POST /v1/decisions` with non-UUID `X-User-Id` returns 400 ProblemDetails (`malformed_user_id`).
- [ ] `POST /v1/decisions` with a parseable but unknown `X-User-Id` returns 401 (`unknown_user`).
- [ ] `POST /v1/decisions` with an unknown `choice` value returns 400 (`unknown_choice`).
- [ ] `POST /v1/decisions` happy path returns 201 + `{ decision_id, cycle_id }`; `users.last_seen_at` is updated in the same transaction.
- [ ] Second `POST /v1/decisions` from same user in same cycle returns 409 (`duplicate`) with `extensions.cycle_id` set.
- [ ] If no cycle is currently open (artificially induced — should never happen in normal operation), returns 503 (`no_open_cycle`).
- [ ] `Choice` lookup is case-insensitive: `"Build"` works the same as `"build"` (covered by a unit test).
- [ ] All error responses have `Content-Type: application/problem+json`.
- [ ] All integration tests pass.

---

## 6. What We Are Deliberately NOT Building in Phase 2

- ❌ The cycle-close worker logic. Phase 3.
- ❌ Aggregation function (`AggregateAndApply`). Phase 3.
- ❌ `GET /v1/world/current`, `GET /v1/world/history`, `GET /v1/events`, `GET /v1/users/{id}/contribution`. Phase 4.
- ❌ Real authentication (passwords, sessions, JWT). Plan-explicit MVP scope.
- ❌ Rate limiting on `POST /v1/users`. Phase 5.
- ❌ Logging configuration beyond .NET defaults. Phase 5.
- ❌ Any kind of pagination. Phase 4.
- ❌ A `GET /v1/users/{id}` endpoint. Not on the master plan; not needed yet.

---

## 7. After Phase 2

The next phase doc (`phase-3-cycle-close-worker.md`) covers:
- Console-app `IHost` for DI parity with the API
- `SELECT ... FOR UPDATE` for the cycle-close transaction
- The pure aggregation function `AggregateAndApply` in `Driftworld.Core` (decimal math + `MidpointRounding.AwayFromZero`)
- Multi-day catch-up loop (per master plan §7)
- Worker integration tests against real Postgres

Once Phase 2 is green, the worker's `Driftworld.Worker/Program.cs` will reuse `AddDriftworldOptions` + `AddDriftworldData` and start exercising the same data the API has been writing.
