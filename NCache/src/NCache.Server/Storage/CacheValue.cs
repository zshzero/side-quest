namespace NCache.Server.Storage;

/// <summary>
/// Represents a value stored in the cache.
///
/// This is a discriminated union — one of a closed set of subtypes.
/// Phase 2 has only StringValue (byte[]), but this type is designed so that
/// Phase 4 can add ListValue, HashValue, SetValue without touching CacheStore.
///
/// The private constructor means no code outside this file can declare a new
/// subtype — the union is "closed". This mirrors how sealed class hierarchies
/// work in Kotlin/Swift and discriminated unions work in F#.
///
/// Using 'record' gives us structural equality and a nice ToString() for free.
/// </summary>
public abstract record CacheValue
{
    // Private constructor — prevents external subtypes. The union is exactly
    // what's declared below, nothing else.
    private CacheValue() { }

    /// <summary>
    /// A binary-safe string value. Data is an arbitrary byte sequence —
    /// we never interpret it as text. The user might store UTF-8, a serialized
    /// Protobuf message, a JPEG, or anything else.
    ///
    /// Phase 4 will add ListValue / HashValue / SetValue here.
    /// </summary>
    public sealed record StringValue(byte[] Data) : CacheValue;
}
