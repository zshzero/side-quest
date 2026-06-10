using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;

namespace NCache.Server.Commands.StringCommands;

/// <summary>
/// PING — health check / liveness probe.
///
/// Redis behavior:
///   PING            → +PONG\r\n  (Simple String)
///   PING message    → $<len>\r\nmessage\r\n  (Bulk String — echo the arg)
///
/// Why is the max-arity check inside the handler instead of in CommandArity?
/// CommandArity has Exact(N) and AtLeast(N) — no Range(min,max). PING is the
/// only Phase-2 command needing a max bound, so a one-line handler check is
/// simpler than introducing a Range variant for a single use case. If a
/// second command needs this pattern, it's worth refactoring.
/// </summary>
public sealed class PingCommand : ICommandHandler
{
    public CommandArity Arity { get; } = new CommandArity.AtLeast(1);

    public RespValue Execute(CommandContext ctx)
    {
        return ctx.ArgCount switch
        {
            1 => new RespValue.SimpleString("PONG"),
            2 => new RespValue.BulkString(ctx.ArgAsBytes(1)),
            _ => CommandErrors.WrongArgCount("PING"),
        };
    }
}
