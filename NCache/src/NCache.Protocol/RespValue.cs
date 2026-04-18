namespace NCache.Protocol;

/// <summary>
/// Represents a value in the RESP2 (Redis Serialization Protocol) format.
/// Every message on the wire is one of these 5 types.
/// </summary>
public abstract record RespValue
{
    // Private constructor prevents anyone outside this file from creating new subtypes.
    // This makes our 5 types below an exhaustive set — similar to a sealed union.
    private RespValue() { }

    /// <summary>
    /// Single-line string for simple status responses. Cannot contain \r\n.
    /// Wire format: +OK\r\n
    /// Used for: status replies like OK, PONG
    /// </summary>
    public sealed record SimpleString(string Value) : RespValue;

    /// <summary>
    /// Error response. The first word is conventionally the error type (e.g., ERR, WRONGTYPE).
    /// Wire format: -ERR unknown command\r\n
    /// Used for: all error responses
    /// </summary>
    public sealed record Error(string Message) : RespValue;

    /// <summary>
    /// 64-bit signed integer.
    /// Wire format: :42\r\n
    /// Used for: counts (DBSIZE, LLEN), boolean-like responses (1/0 for EXISTS, DEL)
    /// </summary>
    public sealed record Integer(long Value) : RespValue;

    /// <summary>
    /// Binary-safe string with explicit length. Can contain any bytes including \r\n.
    /// Wire format: $5\r\nhello\r\n  (length, then data, then trailing CRLF)
    /// Null variant: $-1\r\n  (represents a missing value, e.g., GET on nonexistent key)
    ///
    /// We store byte[] instead of string because RESP is binary-safe.
    /// Commands will convert to string via UTF-8 when they know the value is text.
    /// </summary>
    public sealed record BulkString(byte[]? Data) : RespValue;

    /// <summary>
    /// Ordered collection of RespValues. Elements can be any type, including nested arrays.
    /// Wire format: *2\r\n$3\r\nGET\r\n$4\r\nname\r\n  (count, then each element)
    /// Null variant: *-1\r\n
    ///
    /// Commands are always sent as Arrays of BulkStrings.
    /// Responses can be arrays of mixed types.
    /// </summary>
    public sealed record Array(RespValue[]? Items) : RespValue;
}
