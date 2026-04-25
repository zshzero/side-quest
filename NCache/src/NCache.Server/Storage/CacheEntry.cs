namespace NCache.Server.Storage;

/// <summary>
/// A single cache entry. Holds the value plus any per-entry metadata.
///
/// Why a wrapper around CacheValue?
/// In Phase 2 this looks pointless — one field. But this type is the seam
/// where Phase 3 will add TTL (ExpiresAt) and Phase 7 will add access metadata
/// (LastAccessedAt, AccessCount) for eviction policies. Keeping the wrapper
/// in place now means future phases don't need to change the signatures of
/// every CacheStore method.
///
/// Why class, not record?
/// Records are designed for immutable data with value semantics. Phase 3+
/// will mutate this entry in place (e.g., refreshing ExpiresAt on access for
/// sliding expiry), which reads more naturally on a class with settable
/// properties than on records with 'with'-expressions.
/// </summary>
public sealed class CacheEntry
{
    public CacheValue Value { get; set; }

    public CacheEntry(CacheValue value)
    {
        Value = value;
    }
}
