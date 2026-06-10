using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// DBSIZE — return the number of keys currently stored.
///
/// Note: ICacheStore.Count is not snapshot-consistent under concurrent
/// mutation (it's ConcurrentDictionary.Count, which walks segments). This
/// matches Redis DBSIZE's relaxed semantics — the answer reflects "roughly
/// now", not a precise instant.
/// </summary>
public sealed class DbsizeCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.Exact(1);

    public RespValue Execute(CommandContext ctx)
    {
        return new RespValue.Integer(ctx.Store.Count);
    }
}
