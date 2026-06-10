using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// DEL key [key ...] — remove one or more keys, return how many were actually
/// removed. Missing keys do NOT count and do NOT cause an error.
///
/// Variadic: AtLeast(2) means "the command name + at least one key."
///
/// Returns: Integer (count of keys deleted, 0 if none existed).
/// </summary>
public sealed class DelCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.AtLeast(2);

    public RespValue Execute(CommandContext ctx)
    {
        // ICacheStore.Delete returns true iff the key existed and was removed.
        // Sum the trues and report the count.
        long deleted = 0;
        for (int i = 1; i < ctx.ArgCount; i++)   // i=0 is the command name
        {
            if (ctx.Store.Delete(ctx.ArgAsString(i)))
                deleted++;
        }
        return new RespValue.Integer(deleted);
    }
}
