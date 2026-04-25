# Phase 2: In-Memory Store + String Commands

## What You'll Learn

- What a key-value cache actually *is*, reduced to its simplest form
- How Redis's single-threaded event loop works — and why we're deliberately choosing a different model
- The **command pattern** — treating each command as a first-class object with its own handler
- **Dispatch tables** vs giant `switch` statements
- `ConcurrentDictionary<TKey, TValue>` — .NET's lock-free, thread-safe hash map
- What "atomic" actually means at the CPU and API level
- How to design for extensibility: values that start as strings but will grow into lists, hashes, and sets

---

## 1. Concepts

### What an in-memory cache actually is

Strip away everything fancy and a cache is just a **hash map** that lives in RAM:

```
key (string)  →  value (bytes)
```

That's it. Add a few features and you get Redis:

| Feature | Phase |
|---|---|
| Basic `SET` / `GET` / `DEL` | **Phase 2 (here)** |
| Time-to-live (expire keys after N seconds) | Phase 3 |
| Lists, hashes, sets as values (not just strings) | Phase 4 |
| Survives server restart (snapshots) | Phase 5 |
| Pub/sub messaging | Phase 6 |
| Evict old entries when memory fills up | Phase 7 |

Phase 2 is the baseline. Everything else extends this.

### Redis's threading model (and why we're not copying it)

**Redis is single-threaded.** One thread runs an event loop: accept connections, read requests, execute command, write response, repeat. No two commands ever run simultaneously.

Why does that work for a database that handles 100k+ ops/sec? Because:
- Each command is tiny (microseconds) — a single thread can churn through millions per second
- No locks, no race conditions, no cache-line contention between CPU cores
- Atomicity is **free** — nothing else is running, so every command is automatically atomic

Sounds great. Why aren't we doing it?

**Because we want to teach .NET concurrency**. The whole point of this project is learning. If we went single-threaded, we'd skip:
- `ConcurrentDictionary`
- Lock-free programming
- The actual problems concurrent code creates

So we're making a deliberate choice: **multi-threaded dispatch** (one task per client) with a **thread-safe store**. This is how Kestrel, YARP, ASP.NET Core, and most modern .NET servers work. It's also how Dragonfly (a Redis alternative in C++) works. There's nothing wrong with the multi-threaded model — Redis just picked the simpler route.

### The Command Pattern

Right now our server has one action: echo. In Phase 2, we'll have seven (`PING`, `SET`, `GET`, `DEL`, `EXISTS`, `KEYS`, `DBSIZE`), growing to 30+ by Phase 8.

**Bad design:** one giant method with a `switch` on command name.

```csharp
switch (commandName) {
    case "PING": return HandlePing(args);
    case "SET": return HandleSet(args, store);
    case "GET": return HandleGet(args, store);
    // ... 30 more cases ...
}
```

This gets unreadable fast. Testing one command in isolation requires building the whole switch. Adding a command means editing the central dispatcher.

**Command Pattern:** each command is its own class that implements a common interface.

```csharp
public interface ICommandHandler
{
    CommandArity Arity { get; }
    RespValue Execute(CommandContext ctx);
}

public class SetCommand : ICommandHandler { ... }
public class GetCommand : ICommandHandler { ... }
```

The dispatcher just looks up the right handler and calls `Execute`. Benefits:

- Each command is a **self-contained unit** — easy to read, easy to test
- Adding a command = adding one class; no central file to edit
- The dispatcher is trivial and doesn't need to know what commands exist
- Open/closed principle (open for extension, closed for modification) — textbook example

### Dispatch Tables vs Switches

The dispatcher needs to map command name → handler. Two options:

**Switch (rejected):**
```csharp
return commandName switch {
    "PING" => _pingHandler,
    "SET" => _setHandler,
    // ...
};
```

**Dispatch table (chosen):**
```csharp
private readonly Dictionary<string, ICommandHandler> _handlers;
public bool TryGetHandler(string name, out ICommandHandler h)
    => _handlers.TryGetValue(name, out h);
```

Why the dictionary wins:
- Registration is data-driven — we can register handlers in a loop, read from config, etc.
- Testing is easier — inject a custom `Dictionary` with fakes
- Case-insensitive lookup is a one-line change (pass `StringComparer.OrdinalIgnoreCase`)
- The dispatcher has zero knowledge of which commands exist

### Atomicity — what it actually means

A `SET` looks simple: write the key → value mapping. But on a multi-core machine, *another thread might read the same key in the middle of your write*. What does the reader see?

Atomicity means: **every observer sees either the old value or the new value, never a half-torn state**.

At the CPU level, aligned pointer writes on x86/x64 are atomic — one store instruction, either done or not done. At the .NET level, object reference assignments are atomic. At the `ConcurrentDictionary` level, `TryAdd` / `AddOrUpdate` / `TryRemove` are each atomic — even though internally they might walk buckets, take short segment locks, etc.

What's NOT atomic:

```csharp
// BROKEN — two operations, another thread can slip between them
if (store.Exists(key))            // step 1: check
    return store.Get(key).Value;  // step 2: act  ← key may have been deleted by now!
```

This is called **check-then-act** or **TOCTOU** (time-of-check-to-time-of-use). Fix: use a single atomic operation.

```csharp
// CORRECT — one atomic call
if (store.TryGet(key, out var value))
    return value;
```

This distinction shows up all over Phase 2.

---

## 2. How Redis Does It

### Command table

Redis has a big table of commands in `commands.def` with rows like:

```
{"get",  getCommand,  2,    "rF",  ...},
{"set",  setCommand,  -3,   "wm",  ...},
```

Each row has:
- Name (`"set"`)
- Function pointer (`setCommand`)
- Arity (`-3` means "at least 3 args"; positive means "exactly N args")
- Flags (`w` = write, `r` = read, `m` = may modify memory)

When a command comes in, Redis:
1. Looks up the name in `commands` (a hash map)
2. Validates arity
3. Calls the function pointer

We're doing structurally the same thing in C# — the function pointer becomes an `ICommandHandler`, the arity gets validated by the dispatcher before calling `Execute`.

### Storage

Redis stores values as `robj` — a tagged union with:
- Type tag: `REDIS_STRING`, `REDIS_LIST`, `REDIS_HASH`, etc.
- Encoding: how it's actually stored (int, embstr, raw, ziplist, hashtable, ...)
- Refcount for sharing
- Actual data

Our equivalent is `CacheValue` — an abstract record with subtypes. Phase 2 only has `StringValue(byte[])`. Phase 4 adds `ListValue`, `HashValue`, `SetValue`.

### Atomic commands via the event loop

Every Redis command is atomic because of the single-threaded loop. No `ConcurrentDictionary` needed. In our design, `ConcurrentDictionary` gives us the same atomicity guarantee per-operation, without requiring a single-threaded execution model.

---

## 3. Design Decisions

### Keys: `string`, not `byte[]`

Redis keys are technically binary-safe. In practice, 99.9% are UTF-8 text. We're going with `string`:

- `ConcurrentDictionary<string, CacheEntry>` works out of the box with value equality
- Lookups don't require custom `IEqualityComparer<byte[]>`
- Debug logs and error messages can print the key as-is

If later we need binary keys (unlikely for a learning project), we can switch to a `ByteArrayKey` struct with a proper `IEqualityComparer`.

### Values: `byte[]`

Values stay binary. A user might `SET` an image file, serialized Protobuf, or anything else. We never interpret the bytes — that's the user's job.

### Case-insensitive command names, case-sensitive keys

Redis: `set`, `SET`, `Set`, `SeT` all work the same. But `SET name` and `SET NAME` are different keys.

We replicate this:
- **Commands:** `Dictionary<string, ICommandHandler>` with `StringComparer.OrdinalIgnoreCase`
- **Keys:** `ConcurrentDictionary<string, CacheEntry>` with `StringComparer.Ordinal` (default — case-sensitive, culture-independent)

Why `Ordinal` not `InvariantCulture`? Ordinal is a byte-by-byte comparison — zero culture awareness, no Turkish-İ surprises, fastest. Keys aren't user-facing text; they're identifiers.

### Synchronous handler signature

```csharp
RespValue Execute(CommandContext ctx);   // sync — note: not Task<RespValue>
```

Why not `async`? Phase 2 handlers only touch memory (ConcurrentDictionary lookups). No I/O, no await points. `async` would just add ceremony and state-machine allocations for zero benefit.

Phase 5 (persistence) and Phase 6 (pub/sub) might need async — we'll revisit the signature then. For now, stay simple.

### Arg type: `ReadOnlyMemory<byte>`

`CommandContext.Args` is `ReadOnlyMemory<byte>[]`, not `string[]`.

- Defers UTF-8 decoding until the handler actually needs it
- Some commands only care about the bytes (e.g., storing a value via `SET`)
- `GET` / `DEL` decode keys to strings; `SET` keeps the value as bytes

Helpers in `CommandContext` make decoding one line: `ctx.KeyAsString(argIndex: 1)`.

### Arity validated in dispatcher, not handlers

We could let each handler check its own arity. But that's duplication — every handler would repeat the same `if (ctx.Args.Length != 2) return error;` pattern. Instead:

```csharp
public interface ICommandHandler
{
    CommandArity Arity { get; }       // declares expected arity
    RespValue Execute(CommandContext ctx);  // trusts args are valid
}

public abstract record CommandArity
{
    public sealed record Exact(int Count) : CommandArity;
    public sealed record AtLeast(int Count) : CommandArity;
}
```

The dispatcher reads `handler.Arity`, validates, and only calls `Execute` if args check out. Handlers focus on **what** not **guards**.

---

## 4. Key .NET APIs

### `ConcurrentDictionary<TKey, TValue>`

A thread-safe hash map. Internally it's a hash table split into N "segments" (typically 16× CPU cores). Each segment has its own lock, so different segments can be written to concurrently.

**Methods you'll actually use:**

| Method | What it does | Atomic? |
|---|---|---|
| `TryAdd(key, value)` | Insert if absent; return false if key exists | Yes |
| `TryGetValue(key, out value)` | Read if present | Yes |
| `TryRemove(key, out value)` | Delete if present; return the old value | Yes |
| `AddOrUpdate(key, add, update)` | Insert or replace — single atomic op | Yes |
| `this[key] = value` (indexer set) | Upsert | Yes |
| `.Count` | Count of entries | **Not snapshot-consistent** |
| `.Keys` / `.Values` | Enumerate — **allocates a snapshot** | Yes but costly |

**Gotchas:**

- **`.Count` walks all segments**. It's O(segments), not O(1). For our use case (DBSIZE), this is fine. Don't call it in a tight loop.
- **`.Keys` returns a snapshot** of the keys at the moment you call it. By the time you iterate, some may have been deleted. Fine for phase 2 semantics — `KEYS` in Redis is similarly loose.
- **`AddOrUpdate` calls your delegate under a lock** — don't do heavy work inside it, don't take other locks.
- **There is no `Replace` that returns the previous value** — you need `AddOrUpdate` + capture the old value from inside the delegate, or use `TryRemove` + `TryAdd` (which is NOT atomic together).

### `StringComparer.OrdinalIgnoreCase`

Passed to the dictionary constructor to make lookups case-insensitive:

```csharp
new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
```

This is the correct choice for ASCII command names. `InvariantCultureIgnoreCase` would handle weird Unicode casing (not relevant for command names like "PING"). `CurrentCultureIgnoreCase` would vary by user locale — never use it for protocol data.

### `Encoding.UTF8.GetString(ReadOnlySpan<byte>)`

The fast, allocation-friendly way to decode bytes to string. Takes a span — no intermediate `byte[]` copy.

```csharp
var keyBytes = ctx.Args[1].Span;
var key = Encoding.UTF8.GetString(keyBytes);
```

### `ReadOnlyMemory<byte>` vs `byte[]` vs `ReadOnlySpan<byte>`

Three closely-related types:

| Type | Can cross `await`? | Lifetime | When to use |
|---|---|---|---|
| `byte[]` | Yes | Heap | Owning a buffer you control |
| `ReadOnlyMemory<byte>` | Yes | Heap | Passing byte ranges around async code |
| `ReadOnlySpan<byte>` | **No** | Stack-only | Tight synchronous hot path |

Our `CommandContext.Args` is `ReadOnlyMemory<byte>[]` because the execution happens inside an async method (read from pipe → dispatch → write to pipe). `ReadOnlySpan` wouldn't work — spans can't live across `await`.

Inside a handler, call `.Span` to get a `ReadOnlySpan<byte>` for zero-allocation work on the bytes.

### `Interlocked` — *not* used in Phase 2

`Interlocked.Increment`, `Interlocked.CompareExchange` — atomic operations on primitive fields. We don't need them in Phase 2 because `ConcurrentDictionary` handles our atomicity. Worth knowing about for Phase 3 (counters), Phase 7 (eviction stats).

---

## 5. Implementation Guide

This is the structure you'll build. Each milestone is independently runnable.

### Folder layout (new files only)

```
src/NCache.Server/
├── Storage/
│   ├── CacheValue.cs         # abstract record + StringValue subtype
│   ├── CacheEntry.cs         # holds CacheValue (prepared for TTL later)
│   ├── ICacheStore.cs        # interface
│   └── CacheStore.cs         # ConcurrentDictionary-backed
└── Commands/
    ├── Infrastructure/
    │   ├── ICommandHandler.cs
    │   ├── CommandArity.cs
    │   ├── CommandContext.cs
    │   ├── CommandRegistry.cs
    │   ├── CommandDispatcher.cs
    │   └── CommandErrors.cs
    └── StringCommands/
        ├── PingCommand.cs
        ├── SetCommand.cs
        ├── GetCommand.cs
        ├── DelCommand.cs
        ├── ExistsCommand.cs
        ├── KeysCommand.cs
        └── DbsizeCommand.cs

tests/NCache.Server.Tests/          # new xUnit project
├── Storage/CacheStoreTests.cs
├── Commands/*CommandTests.cs
├── Commands/CommandDispatcherTests.cs
└── Integration/TcpIntegrationTests.cs
└── Integration/ConcurrencyStressTests.cs
```

### Type sketches

**`CacheValue.cs`** — discriminated union for stored values. Phase 2 only has one variant, but we're setting up for phase 4.

```csharp
public abstract record CacheValue
{
    private CacheValue() { }
    public sealed record StringValue(byte[] Data) : CacheValue;
}
```

**`CacheEntry.cs`** — wrapper. Single field now; phase 3 will add `DateTime? ExpiresAt`.

```csharp
public sealed class CacheEntry
{
    public CacheValue Value { get; set; }
}
```

Why `class` not `record`? Phase 3 mutates `ExpiresAt` in place on access (sliding TTL). Easier with a class.

**`ICacheStore.cs`** — the contract.

```csharp
public interface ICacheStore
{
    bool TryGet(string key, out CacheEntry entry);
    void Set(string key, CacheValue value);
    bool Delete(string key);            // returns true if it existed
    bool Exists(string key);
    int Count { get; }
    IEnumerable<string> Keys();
}
```

**`CacheStore.cs`** — the implementation. Wraps `ConcurrentDictionary<string, CacheEntry>`.

Important detail: **defensive copy on SET**. If the caller passes `byte[]` and later mutates it, the stored value shouldn't change. Copy the bytes inside `Set`.

**`CommandArity.cs`**

```csharp
public abstract record CommandArity
{
    private CommandArity() { }
    public sealed record Exact(int Count) : CommandArity;
    public sealed record AtLeast(int Count) : CommandArity;
    public bool Matches(int argCount) => this switch
    {
        Exact e => argCount == e.Count,
        AtLeast a => argCount >= a.Count,
        _ => false
    };
}
```

**`CommandContext.cs`** — context passed to every handler.

```csharp
public readonly record struct CommandContext(
    ICacheStore Store,
    ReadOnlyMemory<byte>[] Args)
{
    public int ArgCount => Args.Length;
    public string ArgAsString(int i) => Encoding.UTF8.GetString(Args[i].Span);
    public byte[] ArgAsBytes(int i) => Args[i].ToArray();  // defensive copy
}
```

**`ICommandHandler.cs`**

```csharp
public interface ICommandHandler
{
    CommandArity Arity { get; }
    RespValue Execute(CommandContext ctx);
}
```

**`CommandRegistry.cs`** — dictionary, case-insensitive command names.

```csharp
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, ICommandHandler handler)
        => _handlers[name] = handler;

    public bool TryGet(string name, out ICommandHandler handler)
        => _handlers.TryGetValue(name, out handler!);
}
```

**`CommandDispatcher.cs`** — the missing piece. Takes a `RespValue`, returns a `RespValue` response.

Pseudocode:

```
Execute(RespValue incoming, ICacheStore store):
    if incoming is not Array or Array.Items is null:
        return CommandErrors.ProtocolError("expected array")
    if Array.Items is empty:
        return CommandErrors.EmptyCommand()
    if Array.Items[0] is not BulkString with non-null data:
        return CommandErrors.InvalidFormat()

    commandName = UTF8.Decode(Array.Items[0].BulkString.Data)

    if registry.TryGet(commandName, out handler) is false:
        return CommandErrors.UnknownCommand(commandName)

    args = Array.Items map (x => x is BulkString ? x.Data.AsMemory() : empty)
    // (full validation that every arg is a BulkString — commands are arrays of bulk strings)

    if not handler.Arity.Matches(args.Length):
        return CommandErrors.WrongArgCount(commandName)

    try:
        return handler.Execute(new CommandContext(store, args))
    catch Exception ex:
        log ex
        return CommandErrors.InternalError()
```

**`CommandErrors.cs`** — central error factories (see plan file for exact wording).

### The seven handlers

Each is ~10 lines. Here's the **Ping** sketch to show the pattern:

```csharp
public sealed class PingCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.AtLeast(1);
    // 1 means just "PING"; 2 means "PING msg"; more = error

    public RespValue Execute(CommandContext ctx)
    {
        if (ctx.ArgCount == 1)
            return new RespValue.SimpleString("PONG");
        if (ctx.ArgCount == 2)
            return new RespValue.BulkString(ctx.ArgAsBytes(1));
        // The dispatcher should prevent > 2, but be defensive
        return CommandErrors.WrongArgCount("PING");
    }
}
```

Other handlers follow the same shape. The plan file has the full per-command spec (arity, edge cases).

### Server wiring change

In `Program.cs`, **before** the `while (!cts.Token.IsCancellationRequested)` accept loop:

```csharp
ICacheStore store = new CacheStore();
var registry = new CommandRegistry();
registry.Register("PING",   new PingCommand());
registry.Register("SET",    new SetCommand());
registry.Register("GET",    new GetCommand());
registry.Register("DEL",    new DelCommand());
registry.Register("EXISTS", new ExistsCommand());
registry.Register("KEYS",   new KeysCommand());
registry.Register("DBSIZE", new DbsizeCommand());
var dispatcher = new CommandDispatcher(registry);
```

Pass `store` and `dispatcher` into `HandleClientAsync`. Inside the inner `while (RespParser.TryParse(...))`, replace:

```csharp
RespSerializer.Write(writer, value!);   // old: echo
```

with:

```csharp
var response = dispatcher.Execute(value!, store);
RespSerializer.Write(writer, response);
```

Everything else in `Program.cs` stays the same — accept loop, per-connection PipeReader/PipeWriter, graceful shutdown, file logging.

---

## 6. Testing Checklist

### Unit: CacheStore
- `Set` then `TryGet` returns the same value
- `Set` twice overwrites
- `TryGet` for missing key returns false
- `Delete` returns true for existing, false for missing
- `Exists` returns correct boolean
- `Count` reflects the number of `Set` calls minus `Delete` calls
- **Defensive copy:** pass a `byte[]`, `Set` it, mutate the input array, assert the stored value is unchanged

### Unit: each command
- Happy path (correct args, correct store state)
- Key-missing path (`GET` returns nil, `EXISTS` returns 0, `DEL` returns 0)
- Overwrite path (`SET` twice, second call still returns OK)
- `PING` with and without arg
- `KEYS *` returns all keys; `KEYS literalname` returns just that key if present
- `DEL a b c` where some exist and some don't — returns count of existing

### Unit: CommandDispatcher
- Non-array input → `-ERR Protocol error: expected array`
- Empty array → `-ERR empty command`
- Non-bulk-string first element → `-ERR invalid command format`
- Unknown command → `-ERR unknown command 'foo'`
- Wrong arity → `-ERR wrong number of arguments for 'set' command`
- Handler throws → `-ERR internal error` (caught, logged)

### Integration: TcpIntegrationTests
- Spin up real server on ephemeral port (`new TcpListener(IPAddress.Loopback, 0)`)
- Connect a real `TcpClient`
- Send RESP-encoded `SET` / `GET` / `DEL` / `DBSIZE`, parse responses, assert expected values
- This test proves the whole stack works together: accept, parse, dispatch, execute, serialize, flush

### Integration: ConcurrencyStressTests
- 100 `Task`s, each doing 1000 operations
- Operations drawn from a mix: 50% SET, 40% GET, 10% DEL
- Keys chosen from a pool of 50 (forces overlap — multiple tasks hit the same key)
- Success criteria:
  - No unhandled exceptions
  - Final `DBSIZE` ≤ 50
  - For keys that were SET and never DEL'd, `GET` returns a valid value
- This test catches race conditions that unit tests can't find

---

## 7. Common Pitfalls

### 1. Check-then-act (TOCTOU)
Classic mistake:
```csharp
if (store.Exists(key))
    return store.TryGet(key, out var entry) ? entry.Value : null;
```
Between `Exists` and `TryGet`, another thread can `Delete`. Use one atomic call:
```csharp
return store.TryGet(key, out var entry) ? entry.Value : null;
```

### 2. Using `ConcurrentDictionary.Count` assuming it's O(1) or snapshot-consistent
It's neither. It walks all segments (which takes segment locks). The count can change the microsecond after you read it. Fine for `DBSIZE` (Redis has the same looseness), but don't use it in a hot loop.

### 3. Forgetting defensive copies
If `Set(key, bytes)` stores the reference and the caller later mutates `bytes`, the stored value corrupts. Copy bytes on the way in.

### 4. `Keys()` returns a live enumeration
`ConcurrentDictionary.Keys` returns a **snapshot collection** — safe to enumerate but represents one moment in time. If you need stability across calls, call `.ToArray()` immediately.

### 5. Mixing `StringComparer.Ordinal` and `InvariantCulture`
Use `Ordinal` for keys (binary, fast). Use `OrdinalIgnoreCase` for command names (case-insensitive, ASCII-only). Never use `InvariantCulture` — it's culture-sensitive for non-ASCII edge cases and slower.

### 6. Validating arity inside handlers instead of the dispatcher
Duplicates logic, easy to drift inconsistent error messages. Let the dispatcher do it once.

### 7. Decoding bytes to string when you don't need to
Every `Encoding.UTF8.GetString` allocates. For `SET`, the value stays as bytes. Only decode when comparing or logging.

### 8. Handler throws → connection dies
Without `try/catch` in the dispatcher, a handler exception propagates up the PipeReader loop and kills the client connection. Catch → return `-ERR internal error` → log internally.

### 9. Case-sensitivity mistake
Case-insensitive commands, case-sensitive keys. Easy to get backwards if you're not paying attention.

### 10. Forgetting to extend the CLI response formatter
Phase 1's CLI formats arrays, integers, bulk strings, simple strings, errors. Phase 2 doesn't *need* CLI changes — the formatter already handles every response type these commands produce. But verify manually.

---

## What's Next

Once Phase 2 is working — you can `SET` / `GET` / `DEL` through `NCache.Cli` or `redis-cli -p 6380` and get real answers — you're ready for **Phase 3: TTL and Expiry**, where keys can expire after a configurable time, and a background service sweeps out dead entries.
