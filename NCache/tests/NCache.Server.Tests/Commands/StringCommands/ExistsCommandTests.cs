using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class ExistsCommandTests
{
    private readonly ExistsCommand _cmd = new();
    private readonly CacheStore _store = new();

    private void Seed(params string[] keys)
    {
        foreach (var k in keys)
            _store.Set(k, new CacheValue.StringValue(System.Text.Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void SingleExistingKey_ReturnsOne()
    {
        Seed("a");

        var response = _cmd.Execute(Ctx(_store, "EXISTS", "a"));

        Assert.Equal(1, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void SingleMissingKey_ReturnsZero()
    {
        var response = _cmd.Execute(Ctx(_store, "EXISTS", "never-set"));

        Assert.Equal(0, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void MultipleKeys_ReturnsCountOfThoseThatExist()
    {
        Seed("a", "c");

        var response = _cmd.Execute(Ctx(_store, "EXISTS", "a", "b", "c", "d"));

        Assert.Equal(2, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void DuplicateKeys_CountedMultipleTimes()
    {
        // The Redis quirk: EXISTS counts arguments, not unique keys.
        Seed("foo");

        var response = _cmd.Execute(Ctx(_store, "EXISTS", "foo", "foo", "foo"));

        Assert.Equal(3, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void DuplicateMissingKeys_StillReturnsZero()
    {
        var response = _cmd.Execute(Ctx(_store, "EXISTS", "missing", "missing"));

        Assert.Equal(0, Assert.IsType<RespValue.Integer>(response).Value);
    }
}
