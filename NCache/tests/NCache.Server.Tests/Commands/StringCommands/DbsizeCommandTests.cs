using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class DbsizeCommandTests
{
    private readonly DbsizeCommand _cmd = new();
    private readonly CacheStore _store = new();

    [Fact]
    public void EmptyStore_ReturnsZero()
    {
        var response = _cmd.Execute(Ctx(_store, "DBSIZE"));

        Assert.Equal(0, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void StoreWithThreeKeys_ReturnsThree()
    {
        _store.Set("a", new CacheValue.StringValue(new byte[] { 1 }));
        _store.Set("b", new CacheValue.StringValue(new byte[] { 1 }));
        _store.Set("c", new CacheValue.StringValue(new byte[] { 1 }));

        var response = _cmd.Execute(Ctx(_store, "DBSIZE"));

        Assert.Equal(3, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void AfterDeletingKey_ReflectsLowerCount()
    {
        _store.Set("a", new CacheValue.StringValue(new byte[] { 1 }));
        _store.Set("b", new CacheValue.StringValue(new byte[] { 1 }));
        _store.Delete("a");

        var response = _cmd.Execute(Ctx(_store, "DBSIZE"));

        Assert.Equal(1, Assert.IsType<RespValue.Integer>(response).Value);
    }
}
