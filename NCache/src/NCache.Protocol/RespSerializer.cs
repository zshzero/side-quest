using System.Buffers;
using System.Text;

namespace NCache.Protocol;

/// <summary>
/// Serializes RespValue objects into RESP2 wire format bytes.
///
/// Writes to IBufferWriter&lt;byte&gt; — an abstraction that PipeWriter implements.
/// This means the serializer works directly with the networking layer
/// without creating intermediate byte[] allocations.
/// </summary>
public static class RespSerializer
{
    /// <summary>
    /// Writes a RespValue to the buffer in RESP2 wire format.
    /// </summary>
    public static void Write(IBufferWriter<byte> writer, RespValue value)
    {
        switch (value)
        {
            case RespValue.SimpleString simple:
                // Wire format: +OK\r\n
                // Simple: prefix byte, the text, CRLF
                WriteByte(writer, RespConstants.SimpleString);
                WriteUtf8(writer, simple.Value);
                WriteCrlf(writer);
                break;

            case RespValue.Error error:
                // Wire format: -ERR something went wrong\r\n
                // Identical structure to SimpleString, just different prefix
                WriteByte(writer, RespConstants.Error);
                WriteUtf8(writer, error.Message);
                WriteCrlf(writer);
                break;

            case RespValue.Integer integer:
                // Wire format: :42\r\n  or  :-1\r\n
                // The number is sent as ASCII text, not as binary bytes.
                // This is a deliberate Redis design choice — readability over compactness.
                WriteByte(writer, RespConstants.Integer);
                WriteUtf8(writer, integer.Value.ToString());
                WriteCrlf(writer);
                break;

            case RespValue.BulkString bulk:
                WriteBulkString(writer, bulk.Data);
                break;

            case RespValue.Array array:
                WriteArray(writer, array.Items);
                break;
        }
    }

    /// <summary>
    /// Bulk Strings are the most complex simple type.
    ///
    /// Non-null: $5\r\nhello\r\n
    ///   - prefix, length as ASCII, CRLF, raw bytes, CRLF
    ///   - The length tells the parser exactly how many bytes to read,
    ///     so the data can contain \r\n or any binary content safely.
    ///
    /// Null: $-1\r\n
    ///   - Special sentinel meaning "this value doesn't exist"
    ///   - This is what GET returns for a missing key
    /// </summary>
    private static void WriteBulkString(IBufferWriter<byte> writer, byte[]? data)
    {
        WriteByte(writer, RespConstants.BulkString);

        if (data is null)
        {
            // Null bulk string: $-1\r\n
            WriteUtf8(writer, "-1");
            WriteCrlf(writer);
            return;
        }

        // Length prefix: tells the parser how many data bytes follow
        WriteUtf8(writer, data.Length.ToString());
        WriteCrlf(writer);

        // The actual data bytes — could be text, binary, anything
        WriteRaw(writer, data);
        WriteCrlf(writer);  // Trailing CRLF after the data (NOT part of the data)
    }

    /// <summary>
    /// Arrays are recursive — each element is itself a RespValue.
    ///
    /// *3\r\n           ← "array of 3 elements"
    /// $3\r\nSET\r\n    ← element 0: BulkString "SET"
    /// $4\r\nname\r\n   ← element 1: BulkString "name"
    /// $5\r\nAlice\r\n  ← element 2: BulkString "Alice"
    ///
    /// The recursive call to Write() handles each element,
    /// regardless of what type it is. Arrays can contain mixed types,
    /// and even nested arrays.
    /// </summary>
    private static void WriteArray(IBufferWriter<byte> writer, RespValue[]? items)
    {
        WriteByte(writer, RespConstants.Array);

        if (items is null)
        {
            // Null array: *-1\r\n
            WriteUtf8(writer, "-1");
            WriteCrlf(writer);
            return;
        }

        // Element count
        WriteUtf8(writer, items.Length.ToString());
        WriteCrlf(writer);

        // Recursively serialize each element
        foreach (var item in items)
        {
            Write(writer, item);
        }
    }

    // ── Low-level write helpers ─────────────────────────────────────────

    /// <summary>
    /// Writes a single byte to the buffer.
    ///
    /// How IBufferWriter works:
    /// 1. GetSpan(1) — "give me a place to write at least 1 byte"
    /// 2. span[0] = b — write the byte
    /// 3. Advance(1) — "I wrote 1 byte, move the position forward"
    ///
    /// This is the zero-allocation pattern. No byte[] is created.
    /// The buffer writer gives you direct access to its internal memory.
    /// </summary>
    private static void WriteByte(IBufferWriter<byte> writer, byte b)
    {
        var span = writer.GetSpan(1);
        span[0] = b;
        writer.Advance(1);
    }

    private static void WriteCrlf(IBufferWriter<byte> writer)
    {
        WriteRaw(writer, RespConstants.CRLF);
    }

    /// <summary>
    /// Converts a string to UTF-8 and writes it directly into the buffer.
    /// </summary>
    private static void WriteUtf8(IBufferWriter<byte> writer, string text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var span = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(text, span);
        writer.Advance(byteCount);
    }

    /// <summary>
    /// Copies raw bytes into the buffer.
    /// </summary>
    private static void WriteRaw(IBufferWriter<byte> writer, byte[] data)
    {
        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);
    }
}
