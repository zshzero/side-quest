using NCache.Server.Storage;

namespace NCache.Server.Tests.Storage;

public class CacheStoreTests
{
    /// <summary>
    /// Convenience: build a StringValue from a UTF-8 string for readable test data.
    /// </summary>
    private static CacheValue.StringValue Str(string text)
        => new(System.Text.Encoding.UTF8.GetBytes(text));

    // ════════════════════════════════════════════════════════════════════
    // Set / TryGet — happy paths
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Set_ThenTryGet_ReturnsStoredValue()
    {
        var store = new CacheStore();

        store.Set("name", Str("Alice"));

        Assert.True(store.TryGet("name", out var entry));
        Assert.NotNull(entry);
        var s = Assert.IsType<CacheValue.StringValue>(entry!.Value);
        Assert.Equal("Alice", System.Text.Encoding.UTF8.GetString(s.Data));
    }

    [Fact]
    public void TryGet_ForMissingKey_ReturnsFalseAndNullEntry()
    {
        var store = new CacheStore();

        Assert.False(store.TryGet("never-set", out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void Set_OnExistingKey_OverwritesPreviousValue()
    {
        var store = new CacheStore();

        store.Set("k", Str("first"));
        store.Set("k", Str("second"));

        store.TryGet("k", out var entry);
        var s = Assert.IsType<CacheValue.StringValue>(entry!.Value);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(s.Data));
    }

    // ════════════════════════════════════════════════════════════════════
    // Delete
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Delete_ExistingKey_ReturnsTrueAndRemovesEntry()
    {
        var store = new CacheStore();
        store.Set("k", Str("v"));

        var deleted = store.Delete("k");

        Assert.True(deleted);
        Assert.False(store.TryGet("k", out _));
    }

    [Fact]
    public void Delete_MissingKey_ReturnsFalse()
    {
        var store = new CacheStore();

        var deleted = store.Delete("never-existed");

        Assert.False(deleted);
    }

    [Fact]
    public void Delete_TwiceOnSameKey_ReturnsTrueThenFalse()
    {
        var store = new CacheStore();
        store.Set("k", Str("v"));

        Assert.True(store.Delete("k"));   // first delete: actually removed
        Assert.False(store.Delete("k"));  // second delete: nothing to remove
    }

    // ════════════════════════════════════════════════════════════════════
    // Exists
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Exists_AfterSet_ReturnsTrue()
    {
        var store = new CacheStore();
        store.Set("k", Str("v"));

        Assert.True(store.Exists("k"));
    }

    [Fact]
    public void Exists_ForMissingKey_ReturnsFalse()
    {
        var store = new CacheStore();

        Assert.False(store.Exists("k"));
    }

    [Fact]
    public void Exists_AfterDelete_ReturnsFalse()
    {
        var store = new CacheStore();
        store.Set("k", Str("v"));
        store.Delete("k");

        Assert.False(store.Exists("k"));
    }

    // ════════════════════════════════════════════════════════════════════
    // Count and Keys
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Count_OnEmptyStore_IsZero()
    {
        var store = new CacheStore();

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Count_TracksSetsAndDeletes()
    {
        var store = new CacheStore();

        store.Set("a", Str("1"));
        store.Set("b", Str("2"));
        store.Set("c", Str("3"));
        Assert.Equal(3, store.Count);

        store.Delete("b");
        Assert.Equal(2, store.Count);

        // Overwriting an existing key shouldn't change the count
        store.Set("a", Str("99"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Keys_ReturnsAllStoredKeys()
    {
        var store = new CacheStore();
        store.Set("alpha", Str("1"));
        store.Set("beta", Str("2"));
        store.Set("gamma", Str("3"));

        var keys = store.Keys().ToHashSet();

        Assert.Equal(3, keys.Count);
        Assert.Contains("alpha", keys);
        Assert.Contains("beta", keys);
        Assert.Contains("gamma", keys);
    }

    [Fact]
    public void Keys_OnEmptyStore_IsEmpty()
    {
        var store = new CacheStore();

        Assert.Empty(store.Keys());
    }

    [Fact]
    public void Keys_ReturnsSnapshot_NotAffectedByLaterMutation()
    {
        // Documents the snapshot semantics: enumerating Keys() while another
        // thread (or this thread) mutates the store does NOT throw, and the
        // already-taken snapshot is unaffected.
        var store = new CacheStore();
        store.Set("a", Str("1"));
        store.Set("b", Str("2"));

        var snapshot = store.Keys().ToList();

        store.Set("c", Str("3"));   // added AFTER snapshot
        store.Delete("a");           // removed AFTER snapshot

        Assert.Equal(2, snapshot.Count);
        Assert.Contains("a", snapshot);
        Assert.Contains("b", snapshot);
    }

    // ════════════════════════════════════════════════════════════════════
    // Case sensitivity — keys are case-sensitive (Redis behavior)
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Keys_AreCaseSensitive()
    {
        var store = new CacheStore();
        store.Set("Name", Str("Alice"));

        Assert.True(store.Exists("Name"));
        Assert.False(store.Exists("name"));   // different key
        Assert.False(store.Exists("NAME"));   // different key
    }

    // ════════════════════════════════════════════════════════════════════
    // Defensive copy — the most important invariant
    //
    // If the caller mutates the byte[] they passed to Set, the stored value
    // must NOT change. Otherwise the cache silently corrupts whenever a
    // caller reuses a buffer.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Set_DoesNotStoreCallersByteArrayByReference()
    {
        var store = new CacheStore();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        store.Set("k", new CacheValue.StringValue(bytes));

        // Mutate the caller's array — this should NOT affect the stored value
        bytes[0] = 99;
        bytes[4] = 99;

        store.TryGet("k", out var entry);
        var stored = Assert.IsType<CacheValue.StringValue>(entry!.Value);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, stored.Data);
    }

    [Fact]
    public void TryGet_ReturnsActualStoredArray_CallerSeesItself()
    {
        // Note: this test documents that TryGet does NOT defensive-copy on
        // read. Phase 2 trusts handlers to be well-behaved (they wrap stored
        // bytes in a RespValue.BulkString and don't mutate). If we ever stop
        // trusting handlers, we'd add a copy here too.
        var store = new CacheStore();
        store.Set("k", new CacheValue.StringValue(new byte[] { 1, 2, 3 }));

        store.TryGet("k", out var first);
        store.TryGet("k", out var second);

        // Both reads return the SAME entry instance — fine because we trust
        // handlers not to mutate.
        Assert.Same(first, second);
    }
}
