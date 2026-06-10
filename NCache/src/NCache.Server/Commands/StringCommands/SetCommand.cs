using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;
using NCache.Server.Storage;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// SET key value — store a value under a key. Always succeeds; overwrites
/// silently if the key already exists.
///
/// Phase 2 supports the basic form only. NX/XX/EX flags arrive in Phase 3
/// alongside TTL.
///
/// Always returns +OK\r\n on success. (Redis returns nil only when NX/XX
/// flags reject the operation — not relevant for Phase 2.)
/// </summary>
public sealed class SetCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.Exact(3);

    public RespValue Execute(CommandContext ctx)
    {
        var key = ctx.ArgAsString(1);
        var value = new CacheValue.StringValue(ctx.ArgAsBytes(2));
        ctx.Store.Set(key, value);
        return new RespValue.SimpleString("OK");
    }
}
