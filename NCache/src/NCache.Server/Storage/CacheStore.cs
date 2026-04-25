using System.Collections.Concurrent;

namespace NCache.Server.Storage;

/// <summary>
/// In-memory cache store backed by a ConcurrentDictionary.
///
/// Why ConcurrentDictionary instead of Dictionary + lock?
/// A single lock serializes every operation across all keys. ConcurrentDictionary
/// internally splits the table into segments (typically 16x CPU-core count), each
/// with its own lock — so different keys can be modified in parallel. For a
/// workload with many independent keys (the typical cache use case), this
/// dramatically reduces contention.
///
/// We use StringComparer.Ordinal explicitly even though it's the default, both
/// for documentation (signals "keys are case-sensitive byte-for-byte") and
/// symmetry with CommandRegistry which uses OrdinalIgnoreCase explicitly.
/// </summary>
public sealed class CacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries
        = new(StringComparer.Ordinal);

    public bool TryGet(string key, out CacheEntry? entry)
    {
        // ConcurrentDictionary.TryGetValue is atomic — either the key is
        // present and we return the entry, or it's absent and we return false.
        // No window where another thread can observe partial state.
        return _entries.TryGetValue(key, out entry);
    }

    public void Set(string key, CacheValue value)
    {
        // Defensive copy: if the caller is storing byte data, clone it before
        // storing. Otherwise the caller could later mutate their byte[] and
        // silently corrupt our cached value.
        //
        // This is a small allocation cost per SET but guarantees the store
        // owns its data exclusively.
        var defensiveValue = value switch
        {
            CacheValue.StringValue s => new CacheValue.StringValue(CloneBytes(s.Data)),
            _ => value
        };

        // Indexer set (= AddOrUpdate) — atomic upsert. Either the key existed
        // (we replace) or it didn't (we insert). Never half-done.
        _entries[key] = new CacheEntry(defensiveValue);
    }

    public bool Delete(string key)
    {
        // TryRemove returns true if it actually removed something, false if
        // the key wasn't there. This boolean is what DEL relies on to count
        // successfully-deleted keys across multiple arguments.
        return _entries.TryRemove(key, out _);
    }

    public bool Exists(string key)
    {
        return _entries.ContainsKey(key);
    }

    public int Count => _entries.Count;
    // Note: ConcurrentDictionary.Count is NOT O(1) — it walks segments.
    // It also takes brief locks. Fine for DBSIZE (called rarely); don't use
    // in tight loops.

    public IEnumerable<string> Keys()
    {
        // .Keys returns a snapshot collection — safe to enumerate even if
        // other threads mutate the dictionary during iteration.
        // ToArray() materializes immediately so the caller can't see ongoing changes.
        return _entries.Keys.ToArray();
    }

    private static byte[] CloneBytes(byte[] source)
    {
        var copy = new byte[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length);
        return copy;
        // Buffer.BlockCopy is the fastest way to copy contiguous bytes —
        // intrinsically vectorized by the JIT.
    }
}
