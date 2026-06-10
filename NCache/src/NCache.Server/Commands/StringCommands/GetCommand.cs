using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;
using NCache.Server.Storage;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// GET key — fetch the string value stored under key.
///
/// Redis behavior:
///   Missing key → nil (BulkString with null Data) — NOT an error
///   Key exists  → the stored bytes as a Bulk String
///
/// Why is "missing" a nil response, not an error?
/// Redis treats reads of absent keys as a normal, common case. The client
/// would have to wrap every GET in error handling otherwise. nil is the
/// designated "not present" sentinel.
///
/// In Phase 4 (lists/hashes/sets), GET on a non-string value returns
/// -WRONGTYPE. The pattern match below already prepares for that branch.
/// </summary>
public sealed class GetCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.Exact(2);

    public RespValue Execute(CommandContext ctx)
    {
        var key = ctx.ArgAsString(1);

        if (!ctx.Store.TryGet(key, out var entry))
            return new RespValue.BulkString(null);  // nil

        return entry!.Value switch
        {
            CacheValue.StringValue s => new RespValue.BulkString(s.Data),
            // Phase 4: add a WRONGTYPE branch for ListValue/HashValue/SetValue
            _ => new RespValue.BulkString(null),
        };
    }
}
