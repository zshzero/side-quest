using NCache.Protocol;
using NCache.Server.Commands.StringCommands;
using NCache.Server.Storage;
using static NCache.Server.Tests.Commands.StringCommands.StringCommandTestHelpers;

namespace NCache.Server.Tests.Commands.StringCommands;

public class KeysCommandTests
{
    private readonly KeysCommand _cmd = new();
    private readonly CacheStore _store = new();

    private void Seed(params string[] keys)
    {
        foreach (var k in keys)
            _store.Set(k, new CacheValue.StringValue(System.Text.Encoding.UTF8.GetBytes("x")));
    }

    private static string[] AsStrings(RespValue response)
    {
        var arr = Assert.IsType<RespValue.Array>(response);
        Assert.NotNull(arr.Items);
        return arr.Items!
            .Select(i => System.Text.Encoding.UTF8.GetString(((RespValue.BulkString)i).Data!))
            .ToArray();
    }

    [Fact]
    public void StarPattern_ReturnsAllKeys()
    {
        Seed("alpha", "beta", "gamma");

        var keys = AsStrings(_cmd.Execute(Ctx(_store, "KEYS", "*")));

        Assert.Equal(3, keys.Length);
        Assert.Contains("alpha", keys);
        Assert.Contains("beta", keys);
        Assert.Contains("gamma", keys);
    }

    [Fact]
    public void StarPatternOnEmptyStore_ReturnsEmptyArray()
    {
        var arr = Assert.IsType<RespValue.Array>(_cmd.Execute(Ctx(_store, "KEYS", "*")));

        Assert.NotNull(arr.Items);
        Assert.Empty(arr.Items!);
    }

    [Fact]
    public void LiteralPatternMatchingExistingKey_ReturnsThatKey()
    {
        Seed("alpha", "beta");

        var keys = AsStrings(_cmd.Execute(Ctx(_store, "KEYS", "alpha")));

        Assert.Single(keys);
        Assert.Equal("alpha", keys[0]);
    }

    [Fact]
    public void LiteralPatternForMissingKey_ReturnsEmptyArray()
    {
        Seed("alpha");

        var arr = Assert.IsType<RespValue.Array>(_cmd.Execute(Ctx(_store, "KEYS", "never-set")));

        Assert.NotNull(arr.Items);
        Assert.Empty(arr.Items!);
    }

    [Fact]
    public void LiteralPatternIsCaseSensitive()
    {
        Seed("Name");

        var lower = AsStrings(_cmd.Execute(Ctx(_store, "KEYS", "name")));
        var exact = AsStrings(_cmd.Execute(Ctx(_store, "KEYS", "Name")));

        Assert.Empty(lower);
        Assert.Single(exact);
    }
}
