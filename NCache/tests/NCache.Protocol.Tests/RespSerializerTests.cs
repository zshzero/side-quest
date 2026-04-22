using System.Buffers;
using System.Text;
using NCache.Protocol;

namespace NCache.Protocol.Tests;

public class RespSerializerTests
{
    /// <summary>
    /// Serializes a RespValue to bytes and returns it as a string for easy assertion.
    /// Uses ArrayBufferWriter which implements IBufferWriter&lt;byte&gt;.
    /// </summary>
    private static string Serialize(RespValue value)
    {
        var writer = new ArrayBufferWriter<byte>();
        RespSerializer.Write(writer, value);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    // ════════════════════════════════════════════════════════════════════
    // Simple String (+)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void SimpleString_OK_SerializesWithPlusPrefixAndCrlf()
    {
        var result = Serialize(new RespValue.SimpleString("OK"));
        Assert.Equal("+OK\r\n", result);
    }

    [Fact]
    public void SimpleString_PONG_SerializesCorrectly()
    {
        var result = Serialize(new RespValue.SimpleString("PONG"));
        Assert.Equal("+PONG\r\n", result);
    }

    [Fact]
    public void SimpleString_Empty_SerializesAsPlusCrlfOnly()
    {
        var result = Serialize(new RespValue.SimpleString(""));
        Assert.Equal("+\r\n", result);
    }

    [Fact]
    public void SimpleString_WithSpaces_PreservesSpaces()
    {
        var result = Serialize(new RespValue.SimpleString("hello world"));
        Assert.Equal("+hello world\r\n", result);
    }

    // ════════════════════════════════════════════════════════════════════
    // Error (-)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Error_WithMessage_SerializesWithDashPrefix()
    {
        var result = Serialize(new RespValue.Error("ERR unknown command 'foo'"));
        Assert.Equal("-ERR unknown command 'foo'\r\n", result);
    }

    [Fact]
    public void Error_EmptyMessage_SerializesAsDashCrlfOnly()
    {
        var result = Serialize(new RespValue.Error(""));
        Assert.Equal("-\r\n", result);
    }

    // ════════════════════════════════════════════════════════════════════
    // Integer (:)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Integer_PositiveValue_SerializesWithColonPrefix()
    {
        var result = Serialize(new RespValue.Integer(42));
        Assert.Equal(":42\r\n", result);
    }

    [Fact]
    public void Integer_Zero_SerializesAsColonZeroCrlf()
    {
        var result = Serialize(new RespValue.Integer(0));
        Assert.Equal(":0\r\n", result);
    }

    [Fact]
    public void Integer_NegativeValue_IncludesMinusSign()
    {
        var result = Serialize(new RespValue.Integer(-1));
        Assert.Equal(":-1\r\n", result);
    }

    [Fact]
    public void Integer_LargeValue_SerializesAsAsciiDigits()
    {
        var result = Serialize(new RespValue.Integer(9999999999));
        Assert.Equal(":9999999999\r\n", result);
    }

    // ════════════════════════════════════════════════════════════════════
    // Bulk String ($)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BulkString_Hello_SerializesWithLengthPrefix()
    {
        var result = Serialize(new RespValue.BulkString("hello"u8.ToArray()));
        Assert.Equal("$5\r\nhello\r\n", result);
    }

    [Fact]
    public void BulkString_EmptyData_SerializesAsZeroLengthWithTrailingCrlf()
    {
        var result = Serialize(new RespValue.BulkString(Array.Empty<byte>()));
        Assert.Equal("$0\r\n\r\n", result);
    }

    [Fact]
    public void BulkString_NullData_SerializesAsNegativeOneLength()
    {
        var result = Serialize(new RespValue.BulkString(null));
        Assert.Equal("$-1\r\n", result);
    }

    [Fact]
    public void BulkString_WithCrlfInData_LengthCountsAllBytes()
    {
        // "ab\r\ncd" = 6 bytes. Length prefix must be 6, not 4.
        var data = Encoding.UTF8.GetBytes("ab\r\ncd");
        var result = Serialize(new RespValue.BulkString(data));
        Assert.Equal("$6\r\nab\r\ncd\r\n", result);
    }

    [Fact]
    public void BulkString_SingleByte_SerializesWithLengthOne()
    {
        var result = Serialize(new RespValue.BulkString("x"u8.ToArray()));
        Assert.Equal("$1\r\nx\r\n", result);
    }

    // ════════════════════════════════════════════════════════════════════
    // Array (*)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Array_SinglePingCommand_SerializesAsArrayOfOneBulkString()
    {
        var value = new RespValue.Array(new RespValue[]
        {
            new RespValue.BulkString("PING"u8.ToArray())
        });

        var result = Serialize(value);
        Assert.Equal("*1\r\n$4\r\nPING\r\n", result);
    }

    [Fact]
    public void Array_SetCommandWithThreeArgs_SerializesAllElements()
    {
        var value = new RespValue.Array(new RespValue[]
        {
            new RespValue.BulkString("SET"u8.ToArray()),
            new RespValue.BulkString("name"u8.ToArray()),
            new RespValue.BulkString("Alice"u8.ToArray()),
        });

        var result = Serialize(value);
        Assert.Equal("*3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nAlice\r\n", result);
    }

    [Fact]
    public void Array_EmptyArray_SerializesAsZeroCount()
    {
        var result = Serialize(new RespValue.Array(Array.Empty<RespValue>()));
        Assert.Equal("*0\r\n", result);
    }

    [Fact]
    public void Array_NullItems_SerializesAsNegativeOneCount()
    {
        var result = Serialize(new RespValue.Array(null));
        Assert.Equal("*-1\r\n", result);
    }

    [Fact]
    public void Array_WithMixedTypes_SerializesEachElementByType()
    {
        var value = new RespValue.Array(new RespValue[]
        {
            new RespValue.SimpleString("OK"),
            new RespValue.Integer(42),
            new RespValue.BulkString("hello"u8.ToArray()),
        });

        var result = Serialize(value);
        Assert.Equal("*3\r\n+OK\r\n:42\r\n$5\r\nhello\r\n", result);
    }

    [Fact]
    public void Array_WithNestedArray_SerializesRecursively()
    {
        // [[1, 2], [3]]
        var value = new RespValue.Array(new RespValue[]
        {
            new RespValue.Array(new RespValue[]
            {
                new RespValue.Integer(1),
                new RespValue.Integer(2),
            }),
            new RespValue.Array(new RespValue[]
            {
                new RespValue.Integer(3),
            }),
        });

        var result = Serialize(value);
        Assert.Equal("*2\r\n*2\r\n:1\r\n:2\r\n*1\r\n:3\r\n", result);
    }

    [Fact]
    public void Array_WithNullBulkStringElement_SerializesNilInsideArray()
    {
        var value = new RespValue.Array(new RespValue[]
        {
            new RespValue.BulkString("hello"u8.ToArray()),
            new RespValue.BulkString(null),
        });

        var result = Serialize(value);
        Assert.Equal("*2\r\n$5\r\nhello\r\n$-1\r\n", result);
    }

    // ════════════════════════════════════════════════════════════════════
    // Round-trip: Serialize → Parse → compare
    //
    // The gold standard: if you serialize a value and parse it back,
    // you should get the same value. Tests both directions at once.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void RoundTrip_SimpleString_SurvivesSerializeAndParse()
    {
        var original = new RespValue.SimpleString("OK");
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void RoundTrip_Error_SurvivesSerializeAndParse()
    {
        var original = new RespValue.Error("ERR something broke");
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void RoundTrip_Integer_SurvivesSerializeAndParse()
    {
        var original = new RespValue.Integer(-42);
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void RoundTrip_BulkString_SurvivesSerializeAndParse()
    {
        var original = new RespValue.BulkString("hello world"u8.ToArray());
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        var bulk = Assert.IsType<RespValue.BulkString>(parsed);
        Assert.Equal(original.Data, bulk.Data);
    }

    [Fact]
    public void RoundTrip_NullBulkString_SurvivesSerializeAndParse()
    {
        var original = new RespValue.BulkString(null);
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void RoundTrip_ComplexArray_SurvivesSerializeAndParse()
    {
        var original = new RespValue.Array(new RespValue[]
        {
            new RespValue.BulkString("SET"u8.ToArray()),
            new RespValue.BulkString("key"u8.ToArray()),
            new RespValue.BulkString("value"u8.ToArray()),
        });

        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        var array = Assert.IsType<RespValue.Array>(parsed);
        Assert.Equal(3, array.Items!.Length);

        // Compare each element's byte data
        for (int i = 0; i < 3; i++)
        {
            var origBulk = (RespValue.BulkString)original.Items![i];
            var parsedBulk = Assert.IsType<RespValue.BulkString>(array.Items[i]);
            Assert.Equal(origBulk.Data, parsedBulk.Data);
        }
    }

    [Fact]
    public void RoundTrip_NullArray_SurvivesSerializeAndParse()
    {
        var original = new RespValue.Array(null);
        var bytes = SerializeToBuffer(original);

        Assert.True(RespParser.TryParse(ref bytes, out var parsed));
        Assert.Equal(original, parsed);
    }

    /// <summary>
    /// Serializes a value into a ReadOnlySequence that can be fed to the parser.
    /// </summary>
    private static ReadOnlySequence<byte> SerializeToBuffer(RespValue value)
    {
        var writer = new ArrayBufferWriter<byte>();
        RespSerializer.Write(writer, value);
        return new ReadOnlySequence<byte>(writer.WrittenMemory);
    }
}
