using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using NCache.Protocol;

// ── Configuration ───────────────────────────────────────────────────────
var port = 6380;

// ── Log file setup ──────────────────────────────────────────────────────
// Logs go to NCache/logs/server.log alongside console output.
// Path.GetDirectoryName twice: bin/Debug/net9.0 → bin/Debug → bin → project root...
// but that's fragile. Instead, we walk up from the exe to find the solution root,
// or just use a well-known relative path from the working directory.
var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDir);
var logFile = Path.Combine(logsDir, "server.log");
var logWriter = new StreamWriter(logFile, append: true) { AutoFlush = true };

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    Console.WriteLine(message);
    logWriter.WriteLine(line);
}

// CancellationTokenSource lets us signal "stop everything" from one place.
// When the user presses Ctrl+C, the Console.CancelKeyPress event fires,
// we call Cancel(), and every async operation watching this token will
// gracefully wind down.
using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    // e.Cancel = true prevents the process from being killed immediately.
    // Instead, our cancellation token triggers and we shut down cleanly.
    e.Cancel = true;
    cts.Cancel();
    Log("Shutting down...");
};

// ── Start listening ─────────────────────────────────────────────────────

// TcpListener wraps a Socket in server mode.
// IPAddress.Loopback (127.0.0.1) = only accept local connections.
// Use IPAddress.Any (0.0.0.0) to accept connections from other machines.
var listener = new TcpListener(IPAddress.Loopback, port);

// Start() begins accepting connections. It takes an optional 'backlog' parameter
// (e.g., listener.Start(128)) that controls the OS-level queue of connections
// waiting to be accepted.
//
// When a client connects, the OS completes the TCP handshake and places the
// connection in this queue. Our AcceptTcpClientAsync pulls from the queue.
// If the queue is full (we're accepting too slowly), the OS rejects new
// connections with a "connection refused" error on the client side.
//
// The no-arg Start() uses the OS default (typically 128 on Linux, varies on Windows).
// For a learning project this is fine — we'd only need to tune it under high
// connection-burst scenarios (hundreds of clients connecting in the same instant).
listener.Start();

Log($"NCache server listening on port {port}");
Log("Press Ctrl+C to stop");

try
{
    // ── Accept loop ─────────────────────────────────────────────────
    // This is the outer loop. Each iteration accepts one new client.
    // It runs forever until Ctrl+C cancels the token.
    while (!cts.Token.IsCancellationRequested)
    {
        // AcceptTcpClientAsync doesn't spin in a loop burning CPU waiting for
        // a client. Under the hood, it registers interest with the OS via an
        // I/O completion port (Windows) or epoll/kqueue (Linux/macOS).
        //
        // The flow:
        // 1. We call AcceptTcpClientAsync → .NET tells the OS "notify me when
        //    a connection arrives on this socket"
        // 2. This thread is released back to the thread pool — it costs nothing
        //    while waiting. No thread is blocked, no CPU is spinning.
        // 3. A client connects → OS completes the TCP handshake, places the
        //    connection in the backlog queue, and "shoulder taps" .NET via the
        //    completion port
        // 4. .NET schedules the continuation (the code after await) on a
        //    thread pool thread, and we resume with the connected TcpClient
        //
        // This is why async/await is so efficient for network servers — a single
        // thread pool can service thousands of awaiting operations because none
        // of them are actually occupying a thread while waiting.
        var tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
        var endpoint = tcpClient.Client.RemoteEndPoint;

        Log($"Client connected: {endpoint}");

        // IMPORTANT: We do NOT await HandleClientAsync here.
        // _ = fires it as a background task so we can immediately loop back
        // and accept the next client. If we awaited, the server could only
        // handle one client at a time.
        _ = HandleClientAsync(tcpClient, endpoint, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // Expected when Ctrl+C fires — the AcceptTcpClientAsync throws this
    // because its cancellation token was triggered. This is normal shutdown.
}
finally
{
    listener.Stop();
    Log("Server stopped.");
    logWriter.Dispose();
}

// ── Per-client handler ──────────────────────────────────────────────────

/// <summary>
/// Handles a single client connection. Each connected client gets its own
/// invocation of this method, running concurrently with other clients.
///
/// The flow:
/// 1. Wrap the TCP socket's NetworkStream in PipeReader/PipeWriter
/// 2. Loop: read bytes → parse RESP → echo back
/// 3. Clean up when the client disconnects or an error occurs
/// </summary>
async Task HandleClientAsync(TcpClient tcpClient, EndPoint? endpoint, CancellationToken ct)
{
    try
    {
        // NetworkStream bridges Socket and Stream.
        // PipeReader/PipeWriter bridge Stream and the Pipelines world.
        //
        // Why not use Socket directly?
        // Pipelines handle buffer management: growing, shrinking, tracking
        // what's been consumed. With raw sockets you'd manage byte[] buffers
        // yourself — the #1 source of bugs in network code.
        await using var stream = tcpClient.GetStream();

        // Why wrap the SAME stream twice, once as reader and once as writer?
        //
        // A TCP connection is bidirectional (full-duplex): bytes flow in both
        // directions simultaneously over the same socket. The NetworkStream
        // exposes both directions:
        //   - stream.Read(...)  pulls bytes the CLIENT sent us
        //   - stream.Write(...) pushes bytes we're sending TO the client
        //
        // These are backed by two separate OS-level buffers (the socket's
        // receive buffer and send buffer), so they operate independently.
        //
        // PipeReader wraps only the read side: "here's what the client said."
        // PipeWriter wraps only the write side: "here's what we're saying back."
        //
        // They never interfere with each other — we could be flushing a response
        // at the same moment new command bytes are arriving from the client.
        // That's the whole point of full-duplex.
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);

        // ── Read loop ───────────────────────────────────────────────
        // Each iteration:
        // 1. ReadAsync: wait for bytes from the client
        // 2. TryParse: attempt to parse a complete RESP message
        // 3. If complete: echo it back. If not: loop and read more bytes.
        while (!ct.IsCancellationRequested)
        {
            // ReadAsync returns when new bytes are available.
            // result.Buffer is a ReadOnlySequence<byte> — possibly non-contiguous.
            ReadResult result = await reader.ReadAsync(ct);
            ReadOnlySequence<byte> buffer = result.Buffer;

            // Try to parse as many complete messages as possible from the buffer.
            // Why a while loop? The client might send multiple commands at once
            // (pipelining), or a single read might contain several small messages.
            while (RespParser.TryParse(ref buffer, out RespValue? value))
            {
                // For now: echo the parsed value back to the client.
                // In Phase 2, this is where command dispatch will happen.
                Log($"[{endpoint}] Received: {FormatForLog(value!)}");

                RespSerializer.Write(writer, value!);
                await writer.FlushAsync(ct);
                // FlushAsync actually sends the bytes to the client.
                // Without it, data sits in the PipeWriter's internal buffer.
            }

            // ── Tell the PipeReader what we consumed ────────────────
            //
            // This is the trickiest part of PipeReader. Two positions:
            //
            // consumed (buffer.Start):
            //   "I processed everything up to here — you can free this memory."
            //   After TryParse's while loop, buffer.Start points to right after
            //   the last successfully parsed message.
            //
            // examined (buffer.End):
            //   "I looked at everything up to here."
            //   Setting this to buffer.End tells the pipe: "Don't wake me up
            //   until NEW bytes arrive beyond this point." Without this, if
            //   there's an incomplete message in the buffer, ReadAsync would
            //   return immediately with the same incomplete data — an infinite loop.
            reader.AdvanceTo(buffer.Start, buffer.End);

            // IsCompleted means the client closed its end of the connection.
            // The stream has ended — no more bytes will ever arrive.
            if (result.IsCompleted)
                break;
        }
    }
    catch (OperationCanceledException)
    {
        // Server is shutting down — normal
    }
    catch (Exception ex)
    {
        Log($"[{endpoint}] Error: {ex.Message}");
    }
    finally
    {
        tcpClient.Close();
        Log($"Client disconnected: {endpoint}");
    }
}

// ── Logging helper ──────────────────────────────────────────────────────

/// <summary>
/// Formats a RespValue for console logging. This is temporary — just for
/// seeing what the echo server receives during development.
/// </summary>
string FormatForLog(RespValue value) => value switch
{
    RespValue.SimpleString s => $"+{s.Value}",
    RespValue.Error e        => $"-{e.Message}",
    RespValue.Integer i      => $":{i.Value}",
    RespValue.BulkString b   => b.Data is null
        ? "(nil)"
        : $"\"{System.Text.Encoding.UTF8.GetString(b.Data)}\"",
    RespValue.Array a        => a.Items is null
        ? "(nil array)"
        : $"[{string.Join(", ", a.Items.Select(FormatForLog))}]",
    _ => value.ToString()!
};
