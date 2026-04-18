using System.Buffers;
using System.Text;

namespace NCache.Protocol;

/// <summary>
/// Parses RESP2 wire format bytes into RespValue objects.
///
/// This is a streaming parser — it handles incomplete data gracefully.
/// TCP delivers a stream of bytes, not discrete messages. A single Read()
/// from the socket might give you:
///   - Exactly one RESP message (lucky case)
///   - Half a message (the rest comes in the next Read)
///   - Three messages plus the start of a fourth
///
/// The TryParse method handles all of these by returning false when
/// it needs more data, without consuming any bytes from the buffer.
/// </summary>
public static class RespParser
{
    /// <summary>
    /// Attempts to parse one complete RespValue from the buffer.
    ///
    /// Returns true if a complete value was parsed:
    ///   - 'value' contains the parsed RespValue
    ///   - 'buffer' is advanced past the consumed bytes
    ///
    /// Returns false if the buffer contains an incomplete message:
    ///   - 'value' is null
    ///   - 'buffer' is NOT modified (no bytes consumed)
    ///     This is critical — the caller will append more bytes and try again.
    /// </summary>
    public static bool TryParse(ref ReadOnlySequence<byte> buffer, out RespValue? value)
    {
        // C# requires 'out' parameters to be assigned on ALL code paths before returning.
        // We set null upfront so early 'return false' paths don't cause a compile error.
        value = null;

        if (buffer.IsEmpty)
            return false;

        // Save the starting position. If parsing fails midway (incomplete data),
        // we need to "rewind" by restoring the original buffer.
        // This is the key insight of the try-parse pattern.
        var originalBuffer = buffer;

        // Peek at the first byte to determine the type
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryRead(out byte prefix))
            return false;

        bool success = prefix switch
        {
            RespConstants.SimpleString => TryParseSimpleString(ref reader, out value),
            RespConstants.Error        => TryParseError(ref reader, out value),
            RespConstants.Integer      => TryParseInteger(ref reader, out value),
            RespConstants.BulkString   => TryParseBulkString(ref reader, out value),
            RespConstants.Array        => TryParseArray(ref reader, out value),
            _ => throw new ProtocolException($"Unknown RESP type prefix: '{(char)prefix}' (0x{prefix:X2})")
        };

        if (success)
        {
            // Advance the buffer past everything the reader consumed.
            // This tells the caller "I used these bytes, you can discard them."
            buffer = buffer.Slice(reader.Consumed);
            return true;
        }

        // Parsing failed (incomplete data). Restore the original buffer
        // so no bytes are consumed. The caller will read more data from
        // the socket and call us again with a larger buffer.
        buffer = originalBuffer;
        value = null;
        return false;
    }

    // ── Type-specific parsers ───────────────────────────────────────────
    //
    // Each of these assumes the prefix byte has already been consumed.
    // They return false if there isn't enough data to complete the parse.

    /// <summary>
    /// Simple String: +OK\r\n
    /// After the '+' prefix, read everything up to \r\n. That's the value.
    /// </summary>
    private static bool TryParseSimpleString(ref SequenceReader<byte> reader, out RespValue? value)
    {
        // 'out' contract: must assign before every return. Null covers the false paths.
        value = null;

        // TryReadLine scans forward looking for \r\n.
        // If found: 'line' contains the bytes BETWEEN the prefix and \r\n.
        // If not found: returns false (incomplete data — \r\n hasn't arrived yet).
        if (!TryReadLine(ref reader, out var line))
            return false;

        value = new RespValue.SimpleString(Encoding.UTF8.GetString(line));
        return true;
    }

    /// <summary>
    /// Error: -ERR unknown command\r\n
    /// Structurally identical to SimpleString, just a different prefix.
    /// </summary>
    private static bool TryParseError(ref SequenceReader<byte> reader, out RespValue? value)
    {
        // 'out' contract: must assign before every return.
        value = null;

        if (!TryReadLine(ref reader, out var line))
            return false;

        value = new RespValue.Error(Encoding.UTF8.GetString(line));
        return true;
    }

    /// <summary>
    /// Integer: :42\r\n or :-1\r\n
    /// The number is ASCII text (not binary), so we read the line and parse it.
    /// </summary>
    private static bool TryParseInteger(ref SequenceReader<byte> reader, out RespValue? value)
    {
        // 'out' contract: must assign before every return.
        value = null;

        if (!TryReadLine(ref reader, out var line))
            return false;

        // The line contains something like "42" or "-1" as ASCII bytes.
        // Convert to string, then parse to long.
        var text = Encoding.UTF8.GetString(line);

        if (!long.TryParse(text, out long number))
            throw new ProtocolException($"Invalid RESP integer: '{text}'");

        value = new RespValue.Integer(number);
        return true;
    }

    /// <summary>
    /// Bulk String: $5\r\nhello\r\n  or  $-1\r\n (null)
    ///
    /// This is the trickiest simple type because it has TWO parts:
    /// 1. The length line: $5\r\n (tells us how many data bytes follow)
    /// 2. The data: hello\r\n (exactly 'length' bytes, then a trailing CRLF)
    ///
    /// The length-prefix is what makes Bulk Strings binary-safe.
    /// Unlike Simple Strings which scan for \r\n in the content,
    /// Bulk Strings read an exact byte count — so the data CAN contain \r\n.
    /// </summary>
    private static bool TryParseBulkString(ref SequenceReader<byte> reader, out RespValue? value)
    {
        // 'out' contract: must assign before every return.
        value = null;

        // Step 1: Read the length line
        if (!TryReadLine(ref reader, out var lengthLine))
            return false;

        var lengthText = Encoding.UTF8.GetString(lengthLine);
        if (!int.TryParse(lengthText, out int length))
            throw new ProtocolException($"Invalid bulk string length: '{lengthText}'");

        // Step 2: Handle null (-1 means "nil")
        if (length == -1)
        {
            value = new RespValue.BulkString(null);
            return true;
        }

        if (length < 0)
            throw new ProtocolException($"Invalid bulk string length: {length}");

        // Step 3: Check if we have enough bytes for the data + trailing \r\n
        // We need exactly 'length' data bytes + 2 bytes for \r\n
        if (reader.Remaining < length + 2)
            return false;  // Not enough data yet — wait for more

        // Step 4: Read exactly 'length' bytes of data
        var data = new byte[length];
        if (!reader.TryCopyTo(data))
            return false;
        reader.Advance(length);

        // Step 5: Consume the trailing \r\n (it's NOT part of the data)
        if (!reader.TryRead(out byte cr) || !reader.TryRead(out byte lf))
            return false;

        if (cr != RespConstants.CR || lf != RespConstants.LF)
            throw new ProtocolException("Bulk string data not followed by CRLF");

        value = new RespValue.BulkString(data);
        return true;
    }

    /// <summary>
    /// Array: *3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nAlice\r\n
    ///
    /// Structure:
    /// 1. Count line: *3\r\n (this array has 3 elements)
    /// 2. Each element: a complete RespValue (parsed recursively)
    ///
    /// This is where the parser becomes recursive. Each element might be
    /// a Simple String, Integer, Bulk String, or even another Array.
    ///
    /// The tricky part: if element 2 of 3 is incomplete, the ENTIRE array
    /// parse fails. We return false, the buffer resets to before the array
    /// started, and we retry when more data arrives.
    /// </summary>
    private static bool TryParseArray(ref SequenceReader<byte> reader, out RespValue? value)
    {
        // 'out' contract: must assign before every return.
        value = null;

        // Step 1: Read the count line
        if (!TryReadLine(ref reader, out var countLine))
            return false;

        var countText = Encoding.UTF8.GetString(countLine);
        if (!int.TryParse(countText, out int count))
            throw new ProtocolException($"Invalid array count: '{countText}'");

        // Step 2: Handle null array
        if (count == -1)
        {
            value = new RespValue.Array(null);
            return true;
        }

        if (count < 0)
            throw new ProtocolException($"Invalid array count: {count}");

        // Step 3: Parse each element recursively
        var items = new RespValue[count];
        for (int i = 0; i < count; i++)
        {
            // Convert the reader's unread bytes back to a ReadOnlySequence
            // so we can call TryParse recursively.
            var remaining = reader.UnreadSequence;

            if (!TryParse(ref remaining, out var item))
                return false;  // Element is incomplete — entire array parse fails

            // TryParse sliced 'remaining' past the consumed bytes.
            // Calculate how many bytes were consumed and advance our reader to match.
            var bytesConsumed = reader.UnreadSequence.Length - remaining.Length;
            reader.Advance(bytesConsumed);

            items[i] = item!;
        }

        value = new RespValue.Array(items);
        return true;
    }

    // ── Helper methods ──────────────────────────────────────────────────

    /// <summary>
    /// Reads bytes until \r\n is found. Returns the content BEFORE the \r\n.
    /// Advances the reader past the \r\n.
    ///
    /// This is used by SimpleString, Error, Integer, and the length/count
    /// lines of BulkString and Array.
    ///
    /// Returns false if \r\n is not found in the remaining buffer
    /// (meaning we need more data from the socket).
    /// </summary>
    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySpan<byte> line)
    {
        // Look for \r\n in the remaining data.
        // We search for \n first (it's a single byte delimiter),
        // then verify that \r precedes it.
        if (reader.TryReadTo(out ReadOnlySequence<byte> lineSequence, RespConstants.LF))
        {
            // lineSequence now contains everything before \n.
            // The reader is positioned after \n.
            // But lineSequence includes the \r — we need to strip it.

            if (lineSequence.Length > 0 &&
                lineSequence.Slice(lineSequence.Length - 1).First.Span[0] == RespConstants.CR)
            {
                // Strip the trailing \r to get just the content
                var content = lineSequence.Slice(0, lineSequence.Length - 1);

                // Convert to contiguous Span (may need to copy if data spans segments)
                line = content.IsSingleSegment
                    ? content.First.Span
                    : content.ToArray();
                return true;
            }

            throw new ProtocolException("Found LF without preceding CR in RESP data");
        }

        // 'out' contract: must assign before every return.
        line = default;
        return false;
    }
}

/// <summary>
/// Thrown when the parser encounters data that violates the RESP2 protocol.
/// This is distinct from "incomplete data" (which returns false from TryParse).
/// This means the data is WRONG, not just unfinished.
/// </summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
