using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class GetCommandTests
{
    private readonly GetCommand _cmd = new();
    private readonly CacheStore _store = new();

    [Fact]
    public void ExistingKey_ReturnsBulkStringWithStoredBytes()
    {
        _store.Set("name", new CacheValue.StringValue(System.Text.Encoding.UTF8.GetBytes("Alice")));

        var response = _cmd.Execute(Ctx(_store, "GET", "name"));

        var bulk = Assert.IsType<RespValue.BulkString>(response);
        Assert.Equal("Alice", bulk.AsString());
    }

    [Fact]
    public void MissingKey_ReturnsNilNotError()
    {
        // Critical Redis contract: GET on a missing key is a normal nil
        // response, NEVER an error. Clients rely on this.
        var response = _cmd.Execute(Ctx(_store, "GET", "never-set"));

        var bulk = Assert.IsType<RespValue.BulkString>(response);
        Assert.Null(bulk.Data);
    }

    [Fact]
    public void EmptyStoredValue_ReturnsZeroLengthBulkString()
    {
        _store.Set("k", new CacheValue.StringValue(Array.Empty<byte>()));

        var response = _cmd.Execute(Ctx(_store, "GET", "k"));

        var bulk = Assert.IsType<RespValue.BulkString>(response);
        Assert.NotNull(bulk.Data);
        Assert.Empty(bulk.Data);
    }

    [Fact]
    public void KeysAreCaseSensitive()
    {
        _store.Set("Name", new CacheValue.StringValue(System.Text.Encoding.UTF8.GetBytes("Alice")));

        // "name" and "NAME" are different keys — should miss
        var lowerResponse = _cmd.Execute(Ctx(_store, "GET", "name"));
        var upperResponse = _cmd.Execute(Ctx(_store, "GET", "NAME"));

        Assert.Null(Assert.IsType<RespValue.BulkString>(lowerResponse).Data);
        Assert.Null(Assert.IsType<RespValue.BulkString>(upperResponse).Data);
    }
}
