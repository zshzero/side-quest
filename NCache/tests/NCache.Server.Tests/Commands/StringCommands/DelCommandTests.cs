using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class DelCommandTests
{
    private readonly DelCommand _cmd = new();
    private readonly CacheStore _store = new();

    private void Seed(params string[] keys)
    {
        foreach (var k in keys)
            _store.Set(k, new CacheValue.StringValue(System.Text.Encoding.UTF8.GetBytes("x")));
    }

    [Fact]
    public void SingleExistingKey_ReturnsOneAndRemovesIt()
    {
        Seed("a");

        var response = _cmd.Execute(Ctx(_store, "DEL", "a"));

        Assert.Equal(1, Assert.IsType<RespValue.Integer>(response).Value);
        Assert.False(_store.Exists("a"));
    }

    [Fact]
    public void SingleMissingKey_ReturnsZero()
    {
        var response = _cmd.Execute(Ctx(_store, "DEL", "never-existed"));

        Assert.Equal(0, Assert.IsType<RespValue.Integer>(response).Value);
    }

    [Fact]
    public void MultipleKeys_ReturnsCountOfThoseThatExisted()
    {
        Seed("a", "c");

        // a exists, b doesn't, c exists, d doesn't → expect 2
        var response = _cmd.Execute(Ctx(_store, "DEL", "a", "b", "c", "d"));

        Assert.Equal(2, Assert.IsType<RespValue.Integer>(response).Value);
        Assert.False(_store.Exists("a"));
        Assert.False(_store.Exists("c"));
    }

    [Fact]
    public void DuplicateKeys_OnlyCountsFirstDeletion()
    {
        Seed("k");

        // Second "k" is already deleted by the first; expect 1
        var response = _cmd.Execute(Ctx(_store, "DEL", "k", "k"));

        Assert.Equal(1, Assert.IsType<RespValue.Integer>(response).Value);
    }
}
