# Phase 5 — Scheduling, Polish, Hand-off

## What You'll Learn

- **ASP.NET Core 10 rate limiting** — `Microsoft.AspNetCore.RateLimiting`, partitioned policies, fixed vs sliding window
- **Per-IP partitioning** — and the `X-Forwarded-For` proxy-trust caveat
- **Serilog vs `Microsoft.Extensions.Logging`** — when each makes sense and why we add Serilog now (not in Phase 1)
- **Structured logging** — log properties as fields, not interpolated strings
- **Rolling-file sinks** — output convention matching the NCache `logs/` layout
- **OS-level scheduling** — a cron line for Linux and a `schtasks` invocation for Windows
- **The `DOTNET_ENVIRONMENT` cron trap** — why a cron-fired worker silently uses Production config unless told otherwise
- **Final master-plan §10 acceptance pass** — what "done" actually means for the MVP

By the end you'll have rate limiting on `POST /v1/users`, structured logs landing in `logs/`, README documentation for both Linux and Windows scheduling, and every checkbox from master plan §10 ticked.

> **Phase 5 is the last phase.** After it lands, the MVP matches the original goal: a shareable backend that runs locally with `docker compose up` + a few `dotnet` commands and behaves correctly under all documented edge cases.

---

## 1. Concepts

### 1.1 Why rate-limit `POST /v1/users` specifically

Per master plan §9 (edge cases): *"Per-IP rate limit on POST /v1/users; per-user posting is already capped at 1/cycle by the unique constraint."*

Translation: every other write endpoint is naturally bounded by the schema. `POST /v1/decisions` can only succeed once per user per cycle. `POST /v1/users` has no such bound — a single attacker can spawn unlimited anonymous users, each of which becomes an authority to post one decision per cycle.

The cheapest abuse vector is therefore **mass user creation**, and the cheapest defense is a per-IP rate limit on the user-creation endpoint. We don't need to rate-limit reads (cheap, idempotent) or decisions (already bounded).

### 1.2 ASP.NET Core 10's rate-limiting middleware

`Microsoft.AspNetCore.RateLimiting` is in the framework — no extra package. The mental model:

- **A policy** is a named rate-limit definition (window, permits per window, queue depth).
- **A partition** is the subset of requests a policy applies to. For per-IP, the partition key is the client IP. For per-user, it'd be the user id.
- **The middleware** sits on the pipeline before endpoint routing or as a `RequireRateLimiting(policyName)` per endpoint.

The shape we want:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("user-create-per-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.OnRejected = WriteProblemDetails429;
});
```

Then on the endpoint:

```csharp
users.MapPost("/", CreateUserAsync).RequireRateLimiting("user-create-per-ip");
```

### 1.3 Fixed window vs sliding window

We pick **fixed window** for MVP. Difference:

- **Fixed window**: counter resets at window boundaries. 5 requests in window N means request 6 is rejected; at the start of window N+1 the counter resets.
- **Sliding window**: continuously rolling. More accurate (no "burst at the boundary" exploit), more memory.

For MVP traffic and a 1-minute window, the boundary-burst exploit is irrelevant. Fixed window is simpler. Document; reconsider if abuse manifests.

### 1.4 Per-IP partitioning behind a load balancer

`HttpContext.Connection.RemoteIpAddress` is the **direct** TCP peer. If the API is behind a load balancer or CDN, that's the LB's IP — not the actual client. Every request from real users would partition into the same bucket and the rate limit becomes a per-LB DOS.

The fix is `X-Forwarded-For` parsing via `UseForwardedHeaders`:

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.KnownProxies.Add(IPAddress.Parse("..."));  // LB's IP — never trust unfiltered XFF
});
```

**Critical**: `KnownProxies` must list the trusted proxy IPs. Otherwise, an attacker sets their own `X-Forwarded-For: 1.2.3.4` and bypasses the limit. For local dev (no LB) we skip this.

We document the production config in the README but ship MVP without `UseForwardedHeaders` — local dev hits the API directly.

### 1.5 Why Serilog now, not in Phase 1

`Microsoft.Extensions.Logging` (MEL) is fine for trivial console output. Serilog gives us:

- **Structured logging** by default — every log property is queryable as a field, not buried in a string.
- **Rolling file sinks** with size + retention controls, no third-party glue.
- **Console formatting** that's actually pretty and grep-able.
- **Sink composition** for future expansion (Seq, Elasticsearch, etc.).

Phase 1 didn't need any of this — `Console.WriteLine` for the seeder was enough. Phase 5 is when we have multiple processes (API + Worker), production deployment plans (cron-fired worker), and the need to debug failures from logs alone.

### 1.6 Structured log shape

```csharp
// Bad — string interpolation, can't query the cycle id later:
log.LogInformation($"Closed cycle {openCycle.Id} with {next.Participants} participants.");

// Good — properties are first-class:
log.LogInformation(
    "Closed cycle {CycleId} with {Participants} participants.",
    openCycle.Id, next.Participants);
```

The property names (`CycleId`, `Participants`) become structured fields in the JSON sink (or the message-template substitution in the console sink). Future log search by `CycleId` becomes one query, not a regex.

### 1.7 Rolling file convention

Match NCache's pattern:

```
Driftworld/
└─ logs/
   ├─ driftworld-api-20260502.log
   ├─ driftworld-api-20260503.log
   ├─ driftworld-worker-20260502.log
   └─ driftworld-worker-20260503.log
```

One file per day, daily rolled, 7-day retention. Path is **relative to current working directory**, which is fine for the API (run from project root) but tricky for the cron-fired worker (its CWD is wherever cron decides).

### 1.8 Cron / Task Scheduler hand-off

The plan settled on `5 0 * * *` UTC (cron) or its `schtasks` equivalent. Two gotchas:

1. **`DOTNET_ENVIRONMENT` is not set in cron contexts.** The Worker defaults to "Production" — which is what we want in production. But it means dev-only `appsettings.Development.json` overrides don't apply. Document.
2. **Working directory** — cron's CWD is `$HOME` by default. We must `cd` to the project root explicitly so relative paths (logs/, appsettings) resolve.

The full Linux entry:

```bash
5 0 * * * cd /opt/driftworld && DOTNET_ENVIRONMENT=Production /usr/bin/dotnet src/Driftworld.Worker/bin/Release/net10.0/Driftworld.Worker.dll >> logs/cron-worker.log 2>&1
```

Windows `schtasks`:

```cmd
schtasks /create /sc daily /st 00:05 /tn "DriftworldWorker" ^
  /tr "cmd /c cd /d C:\driftworld && set DOTNET_ENVIRONMENT=Production && dotnet src\Driftworld.Worker\bin\Release\net10.0\Driftworld.Worker.dll >> logs\schedule.log 2>&1"
```

(Both assume a published Release build. For a dev environment the same idea but `dotnet run --project src/Driftworld.Worker`.)

### 1.9 ProblemDetails for 429

The rate-limiter middleware's default rejected response is HTTP 503 (?!) with no body. We override to 429 with `application/problem+json` matching every other error in the system:

```json
{
  "type": "https://driftworld/errors/rate-limit-exceeded",
  "title": "Rate limit exceeded",
  "status": 429,
  "detail": "Too many user-creation attempts from your IP. Try again after the window resets.",
  "instance": "/v1/users",
  "code": "rate_limit_exceeded",
  "retry_after_seconds": 60
}
```

Implementation: `RateLimiterOptions.OnRejected` callback writes the response directly via `IProblemDetailsService`. We extract a small `ProblemDetailsWriter` helper so this and `DriftworldExceptionHandler` share the format.

---

## 2. How Each Piece Looks

### 2.1 `ProblemDetailsWriter` helper

```csharp
namespace Driftworld.Api.ErrorHandling;

public static class ProblemDetailsWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        IProblemDetailsService problemDetails,
        int status,
        string code,
        string title,
        string detail,
        IDictionary<string, object?>? extensions = null,
        CancellationToken ct = default)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Type = $"https://driftworld/errors/{code.Replace('_', '-')}",
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = code;
        if (extensions is not null)
            foreach (var (k, v) in extensions) problem.Extensions[k] = v;

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
        });
    }
}
```

`DriftworldExceptionHandler` is refactored to call this. The rate-limiter `OnRejected` callback resolves `IProblemDetailsService` from `HttpContext.RequestServices` and calls the same helper.

### 2.2 Rate-limit registration

```csharp
builder.Services.Configure<RateLimitOptions>(
    builder.Configuration.GetSection("Driftworld:RateLimit"));

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("user-create-per-ip", httpContext =>
    {
        var cfg = httpContext.RequestServices
            .GetRequiredService<IOptions<RateLimitOptions>>().Value.UserCreate;
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = cfg.PermitLimit,
                Window = TimeSpan.FromSeconds(cfg.WindowSeconds),
                QueueLimit = 0,
            });
    });

    options.OnRejected = async (context, ct) =>
    {
        var problemDetails = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();
        var retryAfter = context.Lease.TryGetMetadata(
            MetadataName.RetryAfter, out var ts) ? (int)ts.TotalSeconds : 60;

        await ProblemDetailsWriter.WriteAsync(
            context.HttpContext, problemDetails,
            status: 429, code: "rate_limit_exceeded",
            title: "Rate limit exceeded",
            detail: "Too many requests from this IP for this endpoint.",
            extensions: new Dictionary<string, object?> { ["retry_after_seconds"] = retryAfter },
            ct: ct);
    };
});
```

Then on the endpoint:

```csharp
group.MapPost("/", CreateUserAsync)
     .RequireRateLimiting("user-create-per-ip");
```

And in the pipeline:

```csharp
app.UseRateLimiter();   // before MapGroup
```

### 2.3 Serilog wiring

```csharp
// Program.cs (Api)
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine("logs", "driftworld-api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7));
```

Worker mirrors this with `driftworld-worker-.log`. Both pull settings (`Serilog:MinimumLevel`, etc.) from `appsettings.json` if present, with sane defaults baked in code.

### 2.4 README scheduling section

Two code blocks:

```bash
# Linux cron (5 minutes after UTC midnight)
5 0 * * * cd /opt/driftworld && DOTNET_ENVIRONMENT=Production \
  /usr/bin/dotnet src/Driftworld.Worker/bin/Release/net10.0/Driftworld.Worker.dll \
  >> logs/cron-worker.log 2>&1
```

```cmd
:: Windows Task Scheduler
schtasks /create /sc daily /st 00:05 /tn "DriftworldWorker" /tr "..."
```

---

## 3. Pitfalls (read this twice)

### 3.1 `app.UseRateLimiter()` ordering

Must be after `app.UseRouting()` (which `WebApplication` does implicitly) and before any `Map*` calls. If you see policies "not applying" but no error, check ordering.

### 3.2 `OnRejected` is `Func<OnRejectedContext, CancellationToken, ValueTask>`

Easy to write `async (...) => { ... }` and forget the `ValueTask` return type. The framework signature is what it is — just match it.

### 3.3 Per-IP behind a real LB silently degrades

If you deploy behind nginx/Caddy/cloud LB without `UseForwardedHeaders` + `KnownProxies`, every request shares one IP partition. The rate limit becomes a global cap (5 user-creates per minute *total*), not per-client.

For MVP we don't deploy behind anything — note in the README. Phase 6+ would add the proxy config when there's a deployment story.

### 3.4 Serilog request logging is opt-in

`UseSerilog()` does NOT automatically log every HTTP request. Add:

```csharp
app.UseSerilogRequestLogging();
```

after `app.UseRouting()`. Without it, you get no request/response logs.

### 3.5 Rolling file path is CWD-relative

`Path.Combine("logs", "driftworld-api-.log")` resolves to `<CWD>/logs/driftworld-api-2026-05-02.log`. For the Worker fired by cron, CWD is `$HOME` unless we `cd` explicitly. The README cron entry does this; document it.

### 3.6 `DOTNET_ENVIRONMENT=Production` in cron context

Without setting it, `Host.CreateApplicationBuilder` defaults to "Production" — which is what we want. But if you accidentally have `appsettings.Development.json` overriding the connection string for a dev DB and your cron entry doesn't set the env explicitly, you'd be running the production worker against the dev DB. Always set the env explicitly in cron.

### 3.7 `RateLimitPartition.GetFixedWindowLimiter` factory lifetime

The `factory: _ => new FixedWindowRateLimiterOptions { ... }` lambda runs **once per partition**, not once per request. If you read config in there, it's snapshotted at first request from that IP. Subsequent config changes (post-MVP `IOptionsMonitor<>`) won't take effect until the partition is evicted. Acceptable for MVP; document if you care later.

### 3.8 Serilog sink for file requires `Serilog.Sinks.File`, console requires `Serilog.Sinks.Console`

Both are separate packages. `Serilog.AspNetCore` bundles `Serilog.Sinks.Console` transitively but NOT `Sinks.File`. Add explicitly.

---

## 4. Code Layout

```
src/Driftworld.Core/
├─ Exceptions/
│  └─ (no new exceptions — rate-limit responses don't go through IExceptionHandler)
└─ (existing)

src/Driftworld.Api/
├─ ErrorHandling/
│  ├─ DriftworldExceptionHandler.cs   # refactored to use ProblemDetailsWriter
│  └─ ProblemDetailsWriter.cs         # NEW
├─ RateLimiting/
│  ├─ RateLimitOptions.cs             # bound from Driftworld:RateLimit
│  └─ RateLimitPolicies.cs            # AddDriftworldRateLimit() extension
└─ Program.cs                         # adds Serilog, rate limit, request logging

src/Driftworld.Worker/
└─ Program.cs                         # adds Serilog

tests/Driftworld.Api.Tests/
└─ RateLimitTests.cs                  # NEW — 6 user-creates → 6th gets 429
```

---

## 5. Definition of Done (Phase 5)

- [ ] `Microsoft.AspNetCore.RateLimiting` configured with a `user-create-per-ip` policy: 5 permits / 60-second window per IP.
- [ ] `POST /v1/users` is the only endpoint with `RequireRateLimiting("user-create-per-ip")`.
- [ ] When the limit trips, the response is 429 `application/problem+json` with `extensions.code = "rate_limit_exceeded"` and `extensions.retry_after_seconds`.
- [ ] Serilog wired in both API and Worker hosts, console + rolling file sinks (daily, 7-day retention).
- [ ] Logs land in `logs/` at the project root.
- [ ] Integration test: 6 `POST /v1/users` in tight succession produces 5×201 + 1×429 with the right ProblemDetails shape.
- [ ] README documents both Linux cron and Windows `schtasks`/Task Scheduler invocations, including the `DOTNET_ENVIRONMENT` requirement.
- [ ] Final master-plan §10 acceptance pass: every checkbox ticks against the running code.

---

## 6. What We Are Deliberately NOT Building in Phase 5

- ❌ `UseForwardedHeaders` for production-LB scenarios. Out of MVP scope; documented.
- ❌ Distributed rate limiting (Redis-backed). The in-memory partition is fine for a single-instance MVP.
- ❌ Sliding-window rate limiting. Fixed window is simpler.
- ❌ Per-user rate limits on `POST /v1/decisions` — already capped by the schema.
- ❌ Log shipping (Seq, Loki, Elastic). Console + rolling file is enough for MVP.
- ❌ Application Insights / OpenTelemetry. Add when there's a deployment story.
- ❌ Auto-rotation of `logs/` to S3 / blob storage. Phase 6+.
- ❌ Health-check endpoints (`/healthz`, `/readyz`). Add when a deployment platform demands them.

---

## 7. After Phase 5

The MVP is done. Master plan §10 ticks. README walks a fresh clone through `docker compose up` → `dotnet ef database update` → `dotnet run --project src/Driftworld.Api -- --seed` → `dotnet run --project src/Driftworld.Api` and the system works.

Future phases (post-MVP) live outside the master plan — likely:
- Real auth (passwords or bearer tokens).
- A web UI consuming the REST API.
- Telemetry / observability stack.
- Multi-instance deployment with shared rate-limit + leader election for the worker.
- Game-design polish: more variables, more rules, longer cycle horizons.
