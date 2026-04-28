using System.Text;
using NCache.Server.Storage;

namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// Everything a command handler needs to do its work.
///
/// Why a readonly record struct?
/// - Stack-allocated: the dispatcher creates one per command, and a struct
///   avoids heap pressure at high throughput
/// - 'readonly' enforces immutability — handlers can't accidentally mutate
///   the args array reference
/// - 'record' gives equality/ToString for free, useful in tests
///
/// Why ReadOnlyMemory<byte>[] for Args, not string[]?
/// Defers UTF-8 decoding to the moment a handler actually needs a string.
/// SET stores raw bytes — never needs string decoding for the value.
/// GET / DEL / EXISTS decode keys, but the helpers below make that one call.
/// Memory&lt;byte&gt; (not Span&lt;byte&gt;) because spans can't cross await points.
/// </summary>
public readonly record struct CommandContext(
    ICacheStore Store,
    ReadOnlyMemory<byte>[] Args)
{
    /// <summary>
    /// Total argument count, including the command name at index 0.
    /// </summary>
    public int ArgCount => Args.Length;

    /// <summary>
    /// Decodes the argument at index i as a UTF-8 string. Use for keys
    /// and protocol-level identifiers.
    /// </summary>
    public string ArgAsString(int i)
        => Encoding.UTF8.GetString(Args[i].Span);

    /// <summary>
    /// Returns the argument at index i as a fresh byte[] copy. Use when
    /// storing values into the cache, where we want a new buffer the caller
    /// owns. (CacheStore.Set will also defensive-copy, but doing it here
    /// makes the handler's intent explicit.)
    /// </summary>
    public byte[] ArgAsBytes(int i)
        => Args[i].ToArray();
}
