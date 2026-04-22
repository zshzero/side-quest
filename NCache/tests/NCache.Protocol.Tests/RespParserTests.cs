using System.Buffers;
using System.Text;
using NCache.Protocol;

namespace NCache.Protocol.Tests;

public class RespParserTests
{
    /// <summary>
    /// Converts a string to a ReadOnlySequence for feeding into TryParse.
    /// Lets us write test data as readable strings like "+OK\r\n" instead of byte arrays.
    /// </summary>
    private static ReadOnlySequence<byte> ToBuffer(string data)
        => new(Encoding.UTF8.GetBytes(data));

    /// <summary>
    /// Shorthand: parse a single value from a string, asserting success.
    /// </summary>
    private static RespValue Parse(string data)
    {
        var buffer = ToBuffer(data);
        Assert.True(RespParser.TryParse(ref buffer, out var value));
        return value!;
    }

    // ════════════════════════════════════════════════════════════════════
    // Simple String (+)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void SimpleString_WithStatusOK_ParsesValueAsOK()
    {
        var result = Parse("+OK\r\n");

        var simple = Assert.IsType<RespValue.SimpleString>(result);
        Assert.Equal("OK", simple.Value);
    }

    [Fact]
    public void SimpleString_WithPONG_ParsesValueAsPONG()
    {
        var result = Parse("+PONG\r\n");

        var simple = Assert.IsType<RespValue.SimpleString>(result);
        Assert.Equal("PONG", simple.Value);
    }

    [Fact]
    public void SimpleString_WithEmptyContent_ParsesAsEmptyString()
    {
        var result = Parse("+\r\n");

        var simple = Assert.IsType<RespValue.SimpleString>(result);
        Assert.Equal("", simple.Value);
    }

    [Fact]
    public void SimpleString_WithSpacesInValue_PreservesSpaces()
    {
        var result = Parse("+hello world\r\n");

        var simple = Assert.IsType<RespValue.SimpleString>(result);
        Assert.Equal("hello world", simple.Value);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error (-)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Error_WithUnknownCommand_ParsesFullMessage()
    {
        var result = Parse("-ERR unknown command 'foo'\r\n");

        var error = Assert.IsType<RespValue.Error>(result);
        Assert.Equal("ERR unknown command 'foo'", error.Message);
    }

    [Fact]
    public void Error_WithWrongTypePrefix_ParsesFullMessage()
    {
        var result = Parse("-WRONGTYPE Operation against a key holding the wrong kind of value\r\n");

        var error = Assert.IsType<RespValue.Error>(result);
        Assert.StartsWith("WRONGTYPE", error.Message);
    }

    [Fact]
    public void Error_WithEmptyMessage_ParsesAsEmptyString()
    {
        var result = Parse("-\r\n");

        var error = Assert.IsType<RespValue.Error>(result);
        Assert.Equal("", error.Message);
    }

    // ════════════════════════════════════════════════════════════════════
    // Integer (:)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Integer_WithPositiveValue_ParsesCorrectly()
    {
        var result = Parse(":42\r\n");

        var integer = Assert.IsType<RespValue.Integer>(result);
        Assert.Equal(42, integer.Value);
    }

    [Fact]
    public void Integer_WithZero_ParsesAsZero()
    {
        var result = Parse(":0\r\n");

        var integer = Assert.IsType<RespValue.Integer>(result);
        Assert.Equal(0, integer.Value);
    }

    [Fact]
    public void Integer_WithNegativeValue_ParsesCorrectly()
    {
        var result = Parse(":-1\r\n");

        var integer = Assert.IsType<RespValue.Integer>(result);
        Assert.Equal(-1, integer.Value);
    }

    [Fact]
    public void Integer_WithLargeValue_ParsesAsLong()
    {
        // Exceeds int32 range — must be stored as long
        var result = Parse(":9999999999\r\n");

        var integer = Assert.IsType<RespValue.Integer>(result);
        Assert.Equal(9999999999L, integer.Value);
    }

    [Fact]
    public void Integer_WithNonNumericValue_ThrowsProtocolException()
    {
        var buffer = ToBuffer(":notanumber\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }

    // ════════════════════════════════════════════════════════════════════
    // Bulk String ($)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BulkString_WithHello_ParsesFiveBytes()
    {
        var result = Parse("$5\r\nhello\r\n");

        var bulk = Assert.IsType<RespValue.BulkString>(result);
        Assert.Equal("hello", Encoding.UTF8.GetString(bulk.Data!));
    }

    [Fact]
    public void BulkString_WithZeroLength_ParsesAsEmptyByteArray()
    {
        var result = Parse("$0\r\n\r\n");

        var bulk = Assert.IsType<RespValue.BulkString>(result);
        Assert.NotNull(bulk.Data);
        Assert.Empty(bulk.Data);
    }

    [Fact]
    public void BulkString_WithNegativeOneLength_ParsesAsNull()
    {
        var result = Parse("$-1\r\n");

        var bulk = Assert.IsType<RespValue.BulkString>(result);
        Assert.Null(bulk.Data);
    }

    [Fact]
    public void BulkString_WithCrlfInsideData_ParsesCorrectly()
    {
        // This is the whole point of bulk strings — binary safety.
        // "ab\r\ncd" is 6 bytes, with \r\n embedded in the middle.
        var result = Parse("$6\r\nab\r\ncd\r\n");

        var bulk = Assert.IsType<RespValue.BulkString>(result);
        Assert.Equal("ab\r\ncd", Encoding.UTF8.GetString(bulk.Data!));
    }

    [Fact]
    public void BulkString_WithSingleCharacter_ParsesCorrectly()
    {
        var result = Parse("$1\r\na\r\n");

        var bulk = Assert.IsType<RespValue.BulkString>(result);
        Assert.Equal("a", Encoding.UTF8.GetString(bulk.Data!));
    }

    [Fact]
    public void BulkString_WithNonAsciiLength_ThrowsProtocolException()
    {
        var buffer = ToBuffer("$abc\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }

    [Fact]
    public void BulkString_WithInvalidNegativeLength_ThrowsProtocolException()
    {
        // Only -1 is valid (null). -2 and below are invalid.
        var buffer = ToBuffer("$-2\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }

    // ════════════════════════════════════════════════════════════════════
    // Array (*)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Array_WithSingleBulkStringPING_ParsesOneElement()
    {
        var result = Parse("*1\r\n$4\r\nPING\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.Single(array.Items!);

        var cmd = Assert.IsType<RespValue.BulkString>(array.Items![0]);
        Assert.Equal("PING", Encoding.UTF8.GetString(cmd.Data!));
    }

    [Fact]
    public void Array_WithThreeBulkStrings_ParsesSETCommand()
    {
        var result = Parse("*3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nAlice\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.Equal(3, array.Items!.Length);

        Assert.Equal("SET",   Encoding.UTF8.GetString(((RespValue.BulkString)array.Items[0]).Data!));
        Assert.Equal("name",  Encoding.UTF8.GetString(((RespValue.BulkString)array.Items[1]).Data!));
        Assert.Equal("Alice", Encoding.UTF8.GetString(((RespValue.BulkString)array.Items[2]).Data!));
    }

    [Fact]
    public void Array_WithZeroElements_ParsesAsEmptyArray()
    {
        var result = Parse("*0\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.NotNull(array.Items);
        Assert.Empty(array.Items);
    }

    [Fact]
    public void Array_WithNegativeOneCount_ParsesAsNullArray()
    {
        var result = Parse("*-1\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.Null(array.Items);
    }

    [Fact]
    public void Array_WithMixedTypes_ParsesEachElementCorrectly()
    {
        // [SimpleString "OK", Integer 42, BulkString "hello"]
        var result = Parse("*3\r\n+OK\r\n:42\r\n$5\r\nhello\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.Equal(3, array.Items!.Length);

        var s = Assert.IsType<RespValue.SimpleString>(array.Items[0]);
        Assert.Equal("OK", s.Value);

        var i = Assert.IsType<RespValue.Integer>(array.Items[1]);
        Assert.Equal(42, i.Value);

        var b = Assert.IsType<RespValue.BulkString>(array.Items[2]);
        Assert.Equal("hello", Encoding.UTF8.GetString(b.Data!));
    }

    [Fact]
    public void Array_WithNestedArrays_ParsesRecursively()
    {
        // [[1, 2], [3]]
        var result = Parse("*2\r\n*2\r\n:1\r\n:2\r\n*1\r\n:3\r\n");

        var outer = Assert.IsType<RespValue.Array>(result);
        Assert.Equal(2, outer.Items!.Length);

        var inner1 = Assert.IsType<RespValue.Array>(outer.Items[0]);
        Assert.Equal(2, inner1.Items!.Length);
        Assert.Equal(1, ((RespValue.Integer)inner1.Items[0]).Value);
        Assert.Equal(2, ((RespValue.Integer)inner1.Items[1]).Value);

        var inner2 = Assert.IsType<RespValue.Array>(outer.Items[1]);
        Assert.Single(inner2.Items!);
        Assert.Equal(3, ((RespValue.Integer)inner2.Items![0]).Value);
    }

    [Fact]
    public void Array_WithNullBulkStringElement_ParsesNilInsideArray()
    {
        // Array containing a null bulk string: [nil]
        var result = Parse("*1\r\n$-1\r\n");

        var array = Assert.IsType<RespValue.Array>(result);
        Assert.Single(array.Items!);

        var bulk = Assert.IsType<RespValue.BulkString>(array.Items![0]);
        Assert.Null(bulk.Data);
    }

    [Fact]
    public void Array_WithInvalidNegativeCount_ThrowsProtocolException()
    {
        var buffer = ToBuffer("*-2\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }

    // ════════════════════════════════════════════════════════════════════
    // Incomplete data — streaming behavior
    //
    // The most important property: when the buffer doesn't contain a
    // complete message, TryParse must return false WITHOUT consuming
    // any bytes. The caller will read more from the socket and retry.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Incomplete_EmptyBuffer_ReturnsFalse()
    {
        var buffer = ToBuffer("");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_PrefixByteOnly_ReturnsFalse()
    {
        var buffer = ToBuffer("+");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_SimpleStringMissingCrlf_ReturnsFalse()
    {
        var buffer = ToBuffer("+OK");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_IntegerMissingCrlf_ReturnsFalse()
    {
        var buffer = ToBuffer(":42");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_BulkStringLengthLineOnly_ReturnsFalse()
    {
        var buffer = ToBuffer("$5\r\n");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_BulkStringPartialData_ReturnsFalse()
    {
        // Got 3 of the 5 expected bytes
        var buffer = ToBuffer("$5\r\nhel");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_BulkStringDataButMissingTrailingCrlf_ReturnsFalse()
    {
        // Got all 5 data bytes but the trailing \r\n hasn't arrived
        var buffer = ToBuffer("$5\r\nhello");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_ArrayCountLineOnly_ReturnsFalse()
    {
        var buffer = ToBuffer("*2\r\n");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_ArrayFirstElementOnlyOfTwo_ReturnsFalse()
    {
        // Array expects 2 elements but only 1 is complete
        var buffer = ToBuffer("*2\r\n$4\r\nPING\r\n$3\r\n");
        Assert.False(RespParser.TryParse(ref buffer, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Incomplete_BufferIsNotModifiedOnFailure()
    {
        // The critical contract: on failure, the buffer ref must stay unchanged
        // so the caller can append more data and retry.
        var buffer = ToBuffer("$5\r\nhel");
        var originalLength = buffer.Length;

        RespParser.TryParse(ref buffer, out _);

        Assert.Equal(originalLength, buffer.Length);
    }

    // ════════════════════════════════════════════════════════════════════
    // Multiple messages in one buffer (pipelining)
    //
    // TCP can deliver multiple RESP messages in a single read.
    // TryParse should parse one and advance the buffer, leaving
    // the rest for the next call.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Pipeline_TwoSimpleStrings_ParsedSequentially()
    {
        var buffer = ToBuffer("+OK\r\n+PONG\r\n");

        Assert.True(RespParser.TryParse(ref buffer, out var first));
        Assert.Equal("OK", ((RespValue.SimpleString)first!).Value);

        Assert.True(RespParser.TryParse(ref buffer, out var second));
        Assert.Equal("PONG", ((RespValue.SimpleString)second!).Value);

        // Nothing left
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Pipeline_TwoCompleteAndOneIncomplete_ParsesTwoThenReturnsFalse()
    {
        var buffer = ToBuffer("+OK\r\n:42\r\n$5\r\nhel");

        Assert.True(RespParser.TryParse(ref buffer, out var first));
        Assert.IsType<RespValue.SimpleString>(first);

        Assert.True(RespParser.TryParse(ref buffer, out var second));
        Assert.IsType<RespValue.Integer>(second);

        // Third message is incomplete
        Assert.False(RespParser.TryParse(ref buffer, out _));

        // The incomplete bytes remain in the buffer
        Assert.False(buffer.IsEmpty);
    }

    // ════════════════════════════════════════════════════════════════════
    // Protocol errors (malformed data)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void MalformedData_UnknownPrefix_ThrowsProtocolException()
    {
        var buffer = ToBuffer("~invalid\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }

    [Fact]
    public void MalformedData_ExclamationPrefix_ThrowsProtocolException()
    {
        var buffer = ToBuffer("!5\r\nhello\r\n");

        Assert.Throws<ProtocolException>(() =>
            RespParser.TryParse(ref buffer, out _));
    }
}
