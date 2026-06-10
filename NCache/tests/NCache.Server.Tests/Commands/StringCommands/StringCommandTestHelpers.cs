using System.Text;
using NCache.Protocol;
using NCache.Server.Commands.Infrastructure;
using NCache.Server.Storage;

namespace NCache.Server.Tests.Commands.StringCommands;

/// <summary>
/// Shared helpers for unit-testing string commands directly (no dispatcher,
/// no TCP). The handler is given a fresh CommandContext built from raw
/// string args; the test asserts on the returned RespValue.
/// </summary>
internal static class StringCommandTestHelpers
{
    /// <summary>
    /// Builds a CommandContext for the given store and string args.
    /// Caller passes args INCLUDING the command name as args[0]
    /// (matching what the dispatcher would produce on the wire).
    /// </summary>
    public static CommandContext Ctx(ICacheStore store, params string[] args)
    {
        var memory = args.Select(a => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(a)).ToArray();
        return new CommandContext(store, memory);
    }

    /// <summary>
    /// UTF-8 string of a stored bulk string's data, for assertion-side compare.
    /// </summary>
    public static string AsString(this RespValue.BulkString bulk)
        => bulk.Data is null ? throw new InvalidOperationException("nil") : Encoding.UTF8.GetString(bulk.Data);
}
