# Phase 1: RESP Protocol + Echo Server

## What You'll Learn
- How Redis clients and servers communicate (the RESP protocol)
- TCP socket programming in .NET
- `System.IO.Pipelines` — the high-performance I/O framework used by Kestrel
- Streaming binary protocol parsing (handling partial reads)
- The "try-parse" pattern for network protocols

---

## 1. Concepts

### What is RESP?

RESP (REdis Serialization Protocol) is the wire protocol Redis uses for client-server communication. It's a text-based protocol with binary-safe extensions — meaning you can read it with your eyes in a terminal, but it can also carry raw binary data.

**Why did Redis choose this design?** Most database protocols are binary (MySQL, PostgreSQL). Redis went with a text-prefix protocol because:
- It's simple to implement (a weekend project can get a working parser)
- It's debuggable — you can literally `telnet` to Redis and type commands
- The performance cost is negligible because Redis is memory-bound, not protocol-bound

### RESP2 Data Types

Every RESP message starts with a single byte that tells you the type, followed by data, terminated by `\r\n` (CRLF).

| Type | Prefix | Example | Meaning |
|------|--------|---------|---------|
| Simple String | `+` | `+OK\r\n` | Success response |
| Error | `-` | `-ERR unknown command\r\n` | Error response |
| Integer | `:` | `:42\r\n` | Numeric response |
| Bulk String | `$` | `$5\r\nhello\r\n` | Binary-safe string (length-prefixed) |
| Array | `*` | `*2\r\n$3\r\nGET\r\n$4\r\nname\r\n` | Ordered collection |

### How a Command Flows

When you type `SET name Alice` in redis-cli, here's what actually goes over the wire:

```
Client sends (as RESP Array of Bulk Strings):
*3\r\n        ← Array with 3 elements
$3\r\n        ← Bulk String of 3 bytes
SET\r\n       ← The command
$4\r\n        ← Bulk String of 4 bytes
name\r\n      ← First argument
$5\r\n        ← Bulk String of 5 bytes
Alice\r\n     ← Second argument

Server responds:
+OK\r\n       ← Simple String
```

**Key insight**: Commands are always sent as RESP Arrays of Bulk Strings. Responses can be any RESP type.

### Bulk Strings vs Simple Strings

- **Simple Strings** (`+`): Cannot contain `\r\n`. Used for short status responses like `OK`.
- **Bulk Strings** (`$`): Length-prefixed, so they CAN contain `\r\n` and any binary data. The length tells you exactly how many bytes to read.

**Null Bulk String**: `$-1\r\n` means "nil" — used when a key doesn't exist.

**Null Array**: `*-1\r\n` — less common, but exists in the spec.

**Empty vs Null**: `$0\r\n\r\n` is an empty string (0 bytes, but it exists). `$-1\r\n` is null (the value doesn't exist at all). This distinction matters — `GET` returns null bulk string for missing keys, not empty string.

### Why Streaming Parsing Matters

TCP is a **stream protocol**, not a message protocol. When you call `socket.Read()`, you might get:
- Exactly one complete RESP message
- Half of a message (the rest arrives in the next read)
- Two complete messages plus the beginning of a third
- A single byte

Your parser must handle all of these cases. This is why we use the **try-parse pattern**: attempt to parse, and if there aren't enough bytes yet, return false and wait for more data.

This is one of the most important systems programming concepts you'll learn in this phase.

---

## 2. How Redis Does It

Redis's RESP parser lives in `networking.c`. It reads from a client buffer and processes one command at a time. Key properties:

- **Single-threaded**: Redis processes commands sequentially. No locks needed for parsing.
- **Inline commands**: Redis also supports a simpler format (`SET name Alice\r\n` without RESP framing) for `telnet` compatibility. We'll skip this in Phase 1.
- **Read buffer**: Redis pre-allocates a 16KB read buffer per client and grows it as needed.
- **Pipelining**: Clients can send multiple commands without waiting for responses. Redis processes them in order and sends all responses back. This works naturally with streaming parsing — you just keep parsing until the buffer is empty.

For our implementation, we'll use `System.IO.Pipelines` instead of manual buffer management. This gives us the same semantics with less code and better performance.

---

## 3. Design Decisions

### RespValue as a Discriminated Union (Records)

Model the 5 RESP types as sealed records inheriting from an abstract base:

```
RespValue (abstract record)
├── SimpleString(string Value)
├── Error(string Message)
├── Integer(long Value)
├── BulkString(byte[]? Data)    ← null = $-1 (nil)
└── Array(RespValue[]? Items)   ← null = *-1 (nil array)
```

**Why records?**
- Structural equality (two `SimpleString("OK")` are equal) — great for assertions in tests
- Pattern matching with `switch` expressions — makes the serializer and formatter clean
- Immutable by default — RESP values don't change after parsing

**Why `byte[]` for BulkString, not `string`?**
RESP is binary-safe. A value could be a serialized protobuf, a JPEG, or raw bytes. Storing as `byte[]` preserves this. You'll convert to/from UTF-8 `string` in command handlers when you know it's text.

### System.IO.Pipelines for Network I/O

Instead of calling `NetworkStream.ReadAsync(byte[] buffer)` and managing buffers yourself, Pipelines gives you:

- **PipeReader**: Returns a `ReadOnlySequence<byte>` (possibly spanning multiple memory segments). You parse what you can, then tell it how far you got.
- **PipeWriter**: You write into it using `IBufferWriter<byte>`, and it handles flushing to the socket.

**Why not raw `NetworkStream`?** Buffer management is the #1 source of bugs in network code:
- Buffer too small → you miss data
- Buffer too large → wasted memory per connection
- Partial reads → complex state tracking

Pipelines solves all of these. It's also what ASP.NET Core's Kestrel uses internally, so learning it has real-world value.

### Try-Parse Pattern

The parser has this signature:

```
bool TryParse(ref ReadOnlySequence<byte> buffer, out RespValue? value)
```

- Returns `true` + the parsed value if a complete RESP frame exists in the buffer
- Returns `false` if the buffer contains an incomplete frame (need more data)
- Advances `buffer` past consumed bytes on success

The calling code (in `ClientSession`) loops:
1. `await PipeReader.ReadAsync()` — get available bytes
2. While `TryParse(ref buffer, out value)` returns true → process the command
3. Call `PipeReader.AdvanceTo(consumed, examined)` — tell the pipe what we used
4. Loop back to step 1

---

## 4. Key .NET APIs

### `System.IO.Pipelines` (NuGet: `System.IO.Pipelines`)

```csharp
// Create a pipe connected to a socket
var stream = new NetworkStream(socket);
var reader = PipeReader.Create(stream);
var writer = PipeWriter.Create(stream);
```

**PipeReader key methods:**
- `ReadAsync()` → returns `ReadResult` containing `ReadOnlySequence<byte>`
- `AdvanceTo(consumed, examined)` → tells the pipe how far you parsed
  - `consumed`: the position up to which data was fully processed (pipe can discard this)
  - `examined`: the position up to which you looked (pipe won't wake you until NEW data arrives after this point)
  - If you set `examined = buffer.End`, you're saying "I looked at everything, wake me when there's more"
  - If you set `consumed = examined`, you consumed everything

**PipeWriter key methods:**
- `GetMemory()` / `GetSpan()` → get a buffer to write into
- `Advance(bytesWritten)` → tell it how much you wrote
- `FlushAsync()` → sends data to the socket

### `ReadOnlySequence<byte>`

This may be **non-contiguous** — it can span multiple memory segments (like a linked list of byte arrays). This happens when data arrives across multiple socket reads.

For parsing, use `SequenceReader<byte>`:
```csharp
var reader = new SequenceReader<byte>(buffer);
reader.TryRead(out byte prefix);        // Read one byte
reader.TryReadTo(out ReadOnlySpan<byte> line, (byte)'\n');  // Read until delimiter
reader.UnreadSpan;                       // Remaining bytes in current segment
reader.Consumed;                         // Total bytes consumed so far
```

**Gotcha**: `SequenceReader<byte>` is a `ref struct` — you can't store it in a field or pass it to async methods. Use it within synchronous parsing methods only.

### `Socket` and `TcpListener`

```csharp
// Server side:
var listener = new TcpListener(IPAddress.Any, 6380);
listener.Start();
var socket = await listener.AcceptSocketAsync(cancellationToken);

// Client side:
var client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 6380);
```

### `Encoding.UTF8`

```csharp
byte[] bytes = Encoding.UTF8.GetBytes("hello");
string text = Encoding.UTF8.GetString(bytes);
```

---

## 5. Implementation Guide

### Step 1: Create the solution structure

```bash
dotnet new sln -n NCache
dotnet new classlib -n NCache.Protocol -o src/NCache.Protocol
dotnet new console -n NCache.Server -o src/NCache.Server
dotnet new console -n NCache.Cli -o src/NCache.Cli
dotnet new xunit -n NCache.Protocol.Tests -o tests/NCache.Protocol.Tests

dotnet sln add src/NCache.Protocol
dotnet sln add src/NCache.Server
dotnet sln add src/NCache.Cli
dotnet sln add tests/NCache.Protocol.Tests

dotnet add src/NCache.Server reference src/NCache.Protocol
dotnet add src/NCache.Cli reference src/NCache.Protocol
dotnet add tests/NCache.Protocol.Tests reference src/NCache.Protocol

# Add Pipelines NuGet to Protocol and Server
dotnet add src/NCache.Protocol package System.IO.Pipelines
dotnet add src/NCache.Server package System.IO.Pipelines
```

### Step 2: Build RespValue (NCache.Protocol/RespValue.cs)

Define the abstract record and 5 sealed derived types. This is straightforward — just model the types described in the Concepts section.

### Step 3: Build RespConstants (NCache.Protocol/RespConstants.cs)

Define constants for the prefix bytes and CRLF:
- `+` = 0x2B (Simple String)
- `-` = 0x2D (Error)
- `:` = 0x3A (Integer)
- `$` = 0x24 (Bulk String)
- `*` = 0x2A (Array)
- `\r` = 0x0D, `\n` = 0x0A

### Step 4: Build RespSerializer (NCache.Protocol/RespSerializer.cs)

Write a static method that takes a `RespValue` and an `IBufferWriter<byte>` and writes the RESP bytes.

Use a `switch` expression on the `RespValue` type:
- `SimpleString`: write `+`, write value bytes, write `\r\n`
- `Error`: write `-`, write message bytes, write `\r\n`
- `Integer`: write `:`, write number as ASCII, write `\r\n`
- `BulkString`: if null → write `$-1\r\n`. Otherwise → write `$`, write length as ASCII, write `\r\n`, write data bytes, write `\r\n`
- `Array`: if null → write `*-1\r\n`. Otherwise → write `*`, write count as ASCII, write `\r\n`, then recursively serialize each element

**Tip**: Use `Encoding.UTF8.GetBytes()` or write directly using `Span<byte>`. For numbers, you can use `Utf8Formatter.TryFormat()` from `System.Buffers.Text` to avoid string allocations.

### Step 5: Build RespParser (NCache.Protocol/RespParser.cs)

This is the hardest part of Phase 1. The parser must:

1. Read the first byte to determine the type
2. For Simple String / Error / Integer: find `\r\n` and extract the content between the prefix and the terminator
3. For Bulk String: parse the length, read exactly that many bytes after the `\r\n`, then expect another `\r\n`
4. For Array: parse the count, then recursively parse that many elements

**The critical contract**: If at ANY point there aren't enough bytes, return `false` without consuming anything. The caller will provide more bytes later.

Implementation approach:
- Create a `SequenceReader<byte>` from the buffer
- Save the starting position before each parse attempt
- If parsing fails midway (incomplete data), reset to the saved position and return false
- If parsing succeeds, update the buffer to skip past the consumed bytes

**Recursive parsing for Arrays**: Each element in an array is itself a `RespValue` that may or may not be complete. You need to parse each element, and if any element is incomplete, the whole array parse fails (return false).

### Step 6: Build the Echo Server (NCache.Server)

A minimal TCP server that:
1. Creates a `TcpListener` on port 6380
2. Accepts connections in a loop
3. For each connection, spawns an async task that:
   a. Creates `PipeReader`/`PipeWriter` from the `NetworkStream`
   b. In a loop: reads from `PipeReader`, parses RESP with `TryParse`, and for now just echoes the parsed value back by serializing it to the `PipeWriter`
4. Handles `CancellationToken` for graceful shutdown (Ctrl+C)

### Step 7: Build the Minimal CLI Client (NCache.Cli)

A simple client that:
1. Connects to `127.0.0.1:6380` via `TcpClient`
2. Creates a RESP Array representing `["PING"]`
3. Serializes and sends it
4. Reads the response, parses it, prints it
5. Exits

This validates the full round-trip: serialize → send → receive → parse → display.

### Step 8: Write Unit Tests

See the Testing Checklist below.

---

## 6. Testing Checklist

### Serializer Tests
- [ ] Simple String: `+OK\r\n`
- [ ] Error: `-ERR something\r\n`
- [ ] Integer: `:42\r\n`, `:0\r\n`, `:-1\r\n`
- [ ] Bulk String: `$5\r\nhello\r\n`
- [ ] Null Bulk String: `$-1\r\n`
- [ ] Empty Bulk String: `$0\r\n\r\n`
- [ ] Array: `*2\r\n$3\r\nGET\r\n$4\r\nname\r\n`
- [ ] Null Array: `*-1\r\n`
- [ ] Empty Array: `*0\r\n`
- [ ] Nested Array: array containing another array

### Parser Tests
- [ ] All the same cases as serializer (round-trip: serialize then parse, verify equality)
- [ ] Partial data: feed half a Bulk String, assert `TryParse` returns false, feed the rest, assert success
- [ ] Multiple messages in one buffer: two complete RESP values back-to-back, assert both parse
- [ ] Bulk String with `\r\n` in the data (binary-safe check)

### Integration Tests
- [ ] Start echo server, connect client, send PING (as RESP array), receive the echo back
- [ ] Send SET command, receive echo, verify bytes match

---

## 7. Common Pitfalls

### 1. Forgetting to handle partial reads
The most common bug. Your parser sees `$5\r\nhe` (only 2 of 5 bytes) and either crashes or returns garbage. Always check that enough bytes exist before reading.

### 2. Off-by-one with CRLF
After a Bulk String's data, there's a trailing `\r\n` that's NOT part of the data. If you forget to skip it, your next parse will start on `\r` and fail.

### 3. SequenceReader position management
If `TryParse` fails partway through (incomplete data), you must not advance the buffer. Save the position before attempting a parse and restore it on failure. With `SequenceReader<byte>`, use `reader.Rewind(reader.Consumed)` or simply work with a copy of the sequence position.

### 4. PipeReader.AdvanceTo confusion
- `consumed` = what you processed (pipe can free this memory)
- `examined` = what you looked at (pipe won't wake you until NEW data arrives beyond this)
- If you parse one complete message but there's leftover data, set `consumed` to the end of the parsed message and `examined` to the end of the buffer

### 5. Forgetting to flush PipeWriter
Writing to `PipeWriter` buffers data. It doesn't go to the socket until you call `FlushAsync()`. If you forget, the client hangs waiting for a response.

### 6. Not handling client disconnects
When a client disconnects, `PipeReader.ReadAsync()` returns `ReadResult.IsCompleted = true`. If you don't check this, your server loops forever on an empty buffer.

### 7. Integer parsing edge cases
RESP integers can be negative (`:−1\r\n`). Make sure your parser handles the minus sign. Also, the length in Bulk Strings and Arrays is sent as ASCII text, not as binary — parse it as a string, then convert to int.

---

## What's Next

Once this phase is working — you can start the echo server, connect with your CLI, and see RESP values round-trip through the wire — you're ready for **Phase 2: In-Memory Store + String Commands**, where the server will actually do something useful with those commands instead of just echoing them back.
