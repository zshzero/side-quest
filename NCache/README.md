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

> **Note:** The server is currently in echo mode — it parses commands and sends them back as-is. Command execution (PING returning PONG, SET/GET storing data) is coming in Phase 2.

## Run Tests

```bash
dotnet test
```
