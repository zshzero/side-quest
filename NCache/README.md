# NCache

A Redis-compatible in-memory cache server built from scratch in C# for learning purposes.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## Project Structure

```
NCache/
  src/
    NCache.Protocol/    # RESP2 protocol parser and serializer
    NCache.Server/      # TCP server (currently echo mode)
    NCache.Cli/         # Interactive command-line client
  tests/
    NCache.Protocol.Tests/  # Unit tests for the protocol library
```

## Build

```bash
cd NCache
dotnet build
```

## Run

### Start the server

```bash
dotnet run --project src/NCache.Server
```

The server listens on `127.0.0.1:6380` (localhost only). Logs are written to both the console and `logs/server.log`.

Press `Ctrl+C` to stop.

### Connect with the CLI

In a separate terminal:

```bash
dotnet run --project src/NCache.Cli
```

Then type commands at the `ncache>` prompt:

```
ncache> PING
ncache> SET name Alice
ncache> GET name
ncache> quit
```

Quoted strings are supported for values with spaces:

```
ncache> SET greeting "hello world"
```

### Connect with redis-cli

Any Redis client that speaks RESP2 can connect:

```bash
redis-cli -p 6380
```

## Supported Commands

Phase 2 supports these commands. All match Redis behavior — `redis-cli -p 6380` works as a drop-in client.

| Command | Arity | Description |
|---|---|---|
| `PING [message]` | 1–2 | Returns `PONG` (no arg) or echoes the message |
| `SET key value` | 3 | Stores value under key. Overwrites silently. |
| `GET key` | 2 | Returns the stored value, or `(nil)` if missing |
| `DEL key [key ...]` | 2+ | Deletes one or more keys; returns the count actually deleted |
| `EXISTS key [key ...]` | 2+ | Returns the count of args that match an existing key (duplicates count multiple times) |
| `KEYS pattern` | 2 | `*` returns all keys; any other pattern is treated as a literal match (full glob support comes in Phase 8) |
| `DBSIZE` | 1 | Returns the number of keys currently stored |

Coming in later phases: TTL/expiry (Phase 3), lists/hashes/sets (Phase 4), persistence (Phase 5), pub/sub (Phase 6), eviction (Phase 7), full glob patterns + INFO/CONFIG (Phase 8).

## Run Tests

```bash
dotnet test
```
