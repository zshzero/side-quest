using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using NCache.Protocol;

// ── Configuration ───────────────────────────────────────────────────────
var host = "127.0.0.1";
var port = 6380;

// ── Connect to server ───────────────────────────────────────────────────

using var tcpClient = new TcpClient();

try
{
    await tcpClient.ConnectAsync(host, port);
}
catch (SocketException)
{
    Console.Error.WriteLine($"Could not connect to {host}:{port}. Is the server running?");
    return;
}

Console.WriteLine($"Connected to {host}:{port}");
Console.WriteLine("Type commands (e.g., PING, SET key value). Type 'quit' to exit.\n");

await using var stream = tcpClient.GetStream();
var pipeReader = PipeReader.Create(stream);
var pipeWriter = PipeWriter.Create(stream);

// ── REPL loop ───────────────────────────────────────────────────────────
// REPL = Read-Eval-Print Loop
// 1. Read: get user input
// 2. Eval: send to server, receive response  (the server "evaluates")
// 3. Print: display the response
// 4. Loop

while (true)
{
    Console.Write("ncache> ");
    var input = Console.ReadLine();

    // Handle exit conditions
    if (input is null)  // Ctrl+C or stdin closed
        break;

    var trimmed = input.Trim();
    if (trimmed.Length == 0)
        continue;

    if (trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase))
        break;

    // ── Parse user input into RESP ──────────────────────────────────
    //
    // The user types:  SET name Alice
    // We need to send:  *3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nAlice\r\n
    //
    // Step 1: Split into tokens:  ["SET", "name", "Alice"]
    // Step 2: Convert each token to a BulkString
    // Step 3: Wrap in a RESP Array
    //
    // Redis protocol rule: commands are ALWAYS sent as Array of Bulk Strings.
    var tokens = TokenizeInput(trimmed);

    var command = new RespValue.Array(
        tokens.Select(t => (RespValue)new RespValue.BulkString(Encoding.UTF8.GetBytes(t)))
              .ToArray()
    );

    // ── Send the command ────────────────────────────────────────────
    RespSerializer.Write(pipeWriter, command);
    await pipeWriter.FlushAsync();

    // ── Read the response ───────────────────────────────────────────
    //
    // Same TryParse loop as the server, but we only expect one response
    // per command (no pipelining from the CLI side).
    RespValue? response = null;

    while (response is null)
    {
        ReadResult result = await pipeReader.ReadAsync();
        var buffer = result.Buffer;

        // TryParse mutates 'buffer' via ref:
        //   - Success: buffer is advanced past the consumed message,
        //     so buffer.Start = "first byte after the response"
        //   - Failure (incomplete): buffer is restored to its original position,
        //     so buffer.Start = "the original start, nothing consumed"
        //
        // Either way, (buffer.Start, buffer.End) expresses exactly what we want:
        //   consumed = buffer.Start → free everything up to here
        //   examined = buffer.End   → don't wake me until NEW bytes arrive
        //
        // So a single AdvanceTo handles both success and incomplete cases.
        RespParser.TryParse(ref buffer, out response);
        pipeReader.AdvanceTo(buffer.Start, buffer.End);

        if (result.IsCompleted && response is null)
        {
            Console.Error.WriteLine("Server closed the connection.");
            return;
        }
    }

    // ── Display the response ────────────────────────────────────────
    Console.WriteLine(FormatResponse(response));
}

Console.WriteLine("Bye!");

// ── Input tokenizer ─────────────────────────────────────────────────────

/// <summary>
/// Splits user input into tokens, respecting double-quoted strings.
///
/// Examples:
///   "SET name Alice"          → ["SET", "name", "Alice"]
///   "SET greeting \"hello world\""  → ["SET", "greeting", "hello world"]
///
/// Without quote handling, "hello world" would split into two tokens.
/// Redis values often contain spaces, so this is essential.
/// </summary>
static string[] TokenizeInput(string input)
{
    var tokens = new List<string>();
    var current = new StringBuilder();
    bool inQuotes = false;

    for (int i = 0; i < input.Length; i++)
    {
        char c = input[i];

        if (c == '"')
        {
            // Toggle quote mode. Don't include the quote character itself.
            inQuotes = !inQuotes;
        }
        else if (c == ' ' && !inQuotes)
        {
            // Space outside quotes = token boundary
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(c);
        }
    }

    // Don't forget the last token (no trailing space to trigger it)
    if (current.Length > 0)
        tokens.Add(current.ToString());

    return tokens.ToArray();
}

// ── Response formatter ──────────────────────────────────────────────────

/// <summary>
/// Formats a RESP response for human-readable display.
/// Mimics redis-cli's output style:
///   SimpleString  → OK
///   Error         → (error) ERR unknown command
///   Integer       → (integer) 42
///   BulkString    → "hello"  or  (nil)
///   Array         → numbered list
/// </summary>
static string FormatResponse(RespValue value, int indent = 0)
{
    var prefix = new string(' ', indent);

    return value switch
    {
        RespValue.SimpleString s => $"{prefix}{s.Value}",

        RespValue.Error e => $"{prefix}(error) {e.Message}",

        RespValue.Integer i => $"{prefix}(integer) {i.Value}",

        RespValue.BulkString b => b.Data is null
            ? $"{prefix}(nil)"
            : $"{prefix}\"{Encoding.UTF8.GetString(b.Data)}\"",

        RespValue.Array a => a.Items is null
            ? $"{prefix}(nil)"
            : FormatArray(a.Items, indent),

        _ => value.ToString()!
    };
}

/// <summary>
/// Formats an array as a numbered list, like redis-cli:
///   1) "value1"
///   2) "value2"
///   3) (nil)
///
/// For empty arrays: (empty array)
/// </summary>
static string FormatArray(RespValue[] items, int indent)
{
    if (items.Length == 0)
        return $"{new string(' ', indent)}(empty array)";

    var sb = new StringBuilder();
    for (int i = 0; i < items.Length; i++)
    {
        if (i > 0) sb.AppendLine();
        sb.Append($"{new string(' ', indent)}{i + 1}) {FormatResponse(items[i]).TrimStart()}");
    }
    return sb.ToString();
}
