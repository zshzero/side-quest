namespace NCache.Protocol;

/// <summary>
/// RESP2 wire format constants.
/// Each RESP type is identified by its first byte (the prefix).
/// All messages are terminated by CRLF (\r\n).
/// </summary>
public static class RespConstants
{
    // Type prefixes — the first byte on the wire tells you what type follows
    public const byte SimpleString = (byte)'+';  // 0x2B
    public const byte Error        = (byte)'-';  // 0x2D
    public const byte Integer      = (byte)':';  // 0x3A
    public const byte BulkString   = (byte)'$';  // 0x24
    public const byte Array        = (byte)'*';  // 0x2A

    // Line terminator — every RESP line ends with these two bytes
    public const byte CR = (byte)'\r';  // 0x0D  Carriage Return
    public const byte LF = (byte)'\n';  // 0x0A  Line Feed

    // Pre-allocated for frequent use in serializer/parser
    public static readonly byte[] CRLF = [(byte)'\r', (byte)'\n'];
}
