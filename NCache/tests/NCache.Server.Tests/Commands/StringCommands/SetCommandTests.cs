using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class SetCommandTests
{
    private readonly SetCommand _cmd = new();
    private readonly CacheStore _store = new();

    [Fact]
    public void NewKey_ReturnsOK_AndStoresValue()
    {
        var response = _cmd.Execute(Ctx(_store, "SET", "name", "Alice"));

        Assert.Equal("OK", Assert.IsType<RespValue.SimpleString>(response).Value);
        Assert.True(_store.TryGet("name", out var entry));
        var stored = Assert.IsType<CacheValue.StringValue>(entry!.Value);
        Assert.Equal("Alice", System.Text.Encoding.UTF8.GetString(stored.Data));
    }

    [Fact]
    public void ExistingKey_OverwritesPreviousValue()
    {
        _cmd.Execute(Ctx(_store, "SET", "k", "first"));
        _cmd.Execute(Ctx(_store, "SET", "k", "second"));

        _store.TryGet("k", out var entry);
        var stored = Assert.IsType<CacheValue.StringValue>(entry!.Value);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(stored.Data));
    }

    [Fact]
    public void EmptyValue_StoresZeroByteValue()
    {
        var response = _cmd.Execute(Ctx(_store, "SET", "k", ""));

        Assert.IsType<RespValue.SimpleString>(response);
        _store.TryGet("k", out var entry);
        var stored = Assert.IsType<CacheValue.StringValue>(entry!.Value);
        Assert.Empty(stored.Data);
    }
}
