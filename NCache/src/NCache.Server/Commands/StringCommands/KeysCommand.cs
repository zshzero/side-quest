using System.Text;
using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// KEYS pattern — return keys matching the pattern.
///
/// Phase 2 supports a deliberately-restricted pattern grammar:
///   "*"           → return all keys
///   "literalkey"  → return that key if it exists, else empty array
///
/// Redis supports full glob patterns (?, [abc], [a-z], wildcard mid-string).
/// Implementing a glob matcher is a separate learning topic — we add it in
/// Phase 8. Trying to half-implement it here would produce subtle bugs.
///
/// Returns: Array of Bulk Strings.
///
/// Performance note: real Redis recommends against KEYS in production
/// because it scans the entire keyspace and blocks the event loop. SCAN
/// is the cursor-based alternative. For our learning project, KEYS is fine.
/// </summary>
public sealed class KeysCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.Exact(2);

    public RespValue Execute(CommandContext ctx)
    {
        var pattern = ctx.ArgAsString(1);

        IEnumerable<string> matches = pattern == "*"
            ? ctx.Store.Keys()
            : ctx.Store.Exists(pattern)
                ? new[] { pattern }
                : Array.Empty<string>();

        var items = matches
            .Select(k => (RespValue)new RespValue.BulkString(Encoding.UTF8.GetBytes(k)))
            .ToArray();

        return new RespValue.Array(items);
    }
}
