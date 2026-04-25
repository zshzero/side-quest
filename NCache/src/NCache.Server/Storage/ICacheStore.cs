namespace NCache.Server.Storage;

/// <summary>
/// The contract for the in-memory key-value store.
///
/// Why an interface?
/// - Tests can substitute a fake implementation
/// - Future phases can swap in alternative implementations (sharded, eviction-aware)
/// - Documents the entire surface area available to commands in one place
///
/// Atomicity contract:
/// Every method below is atomic with respect to itself — no other thread can
/// observe a half-completed operation. However, COMBINING two calls (e.g.,
/// Exists then TryGet) is NOT atomic. Always prefer a single call.
/// </summary>
public interface ICacheStore
{
    /// <summary>
    /// Attempts to retrieve the entry for the given key.
    /// Returns true if the key exists; entry is set to the stored entry.
    /// Returns false if the key is absent; entry is null.
    ///
    /// Prefer this over Exists+Get — it's a single atomic operation.
    /// </summary>
    bool TryGet(string key, out CacheEntry? entry);

    /// <summary>
    /// Inserts or overwrites the value for the given key.
    /// Always succeeds (Redis SET semantics — no NX/XX flags in phase 2).
    /// </summary>
    void Set(string key, CacheValue value);

    /// <summary>
    /// Removes the key if present.
    /// Returns true if the key existed and was deleted, false if it didn't exist.
    /// The boolean is what DEL uses to count successfully-deleted keys.
    /// </summary>
    bool Delete(string key);

    /// <summary>
    /// Returns true if the key exists. Convenience method for EXISTS.
    /// </summary>
    bool Exists(string key);

    /// <summary>
    /// Total number of keys currently stored.
    /// Note: not snapshot-consistent under concurrent mutation —
    /// matches Redis DBSIZE's relaxed semantics.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Returns a snapshot of all keys.
    /// The snapshot is taken when the method is called; concurrent mutations
    /// after that point are not reflected in the returned enumeration.
    /// </summary>
    IEnumerable<string> Keys();
}
