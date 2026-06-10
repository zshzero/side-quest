using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class PingCommandTests
{
    private readonly PingCommand _cmd = new();
    private readonly CacheStore _store = new();

    [Fact]
    public void NoArgs_ReturnsSimpleStringPONG()
    {
        var response = _cmd.Execute(Ctx(_store, "PING"));

        var simple = Assert.IsType<RespValue.SimpleString>(response);
        Assert.Equal("PONG", simple.Value);
    }

    [Fact]
    public void OneArg_ReturnsBulkStringEchoingTheArg()
    {
        var response = _cmd.Execute(Ctx(_store, "PING", "hello"));

        var bulk = Assert.IsType<RespValue.BulkString>(response);
        Assert.Equal("hello", bulk.AsString());
    }

    [Fact]
    public void TooManyArgs_ReturnsWrongArgCountError()
    {
        // Defensive: dispatcher should prevent this, but the handler still
        // refuses gracefully if it ever bypasses the dispatcher.
        var response = _cmd.Execute(Ctx(_store, "PING", "a", "b"));

        var error = Assert.IsType<RespValue.Error>(response);
        Assert.Contains("wrong number of arguments", error.Message);
    }
}
