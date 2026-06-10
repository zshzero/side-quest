using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// EXISTS key [key ...] — return how many of the given keys exist.
///
/// Quirk: duplicate keys count multiple times (Redis behavior).
///   EXISTS foo foo  → 2 if foo exists
///   EXISTS foo foo  → 0 if foo doesn't exist
/// This matches Redis exactly. Don't deduplicate — the count is over args,
/// not unique keys.
///
/// Returns: Integer (count of args that match an existing key, with duplicates).
/// </summary>
public sealed class ExistsCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.AtLeast(2);

    public RespValue Execute(CommandContext ctx)
    {
        long count = 0;
        for (int i = 1; i < ctx.ArgCount; i++)
        {
            if (ctx.Store.Exists(ctx.ArgAsString(i)))
                count++;
        }
        return new RespValue.Integer(count);
    }
}
