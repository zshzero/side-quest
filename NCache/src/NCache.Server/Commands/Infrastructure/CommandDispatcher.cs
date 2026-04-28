using NCache.Protocol;
using NCache.Server.Storage;

namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// Takes a parsed RESP message and produces a RESP response.
///
/// This class owns the "command shape contract":
///   - Top-level message must be an Array
///   - Array must be non-empty
///   - Every element must be a BulkString with non-null data
///   - The first BulkString's data is the command name (UTF-8)
///   - The handler's declared arity must match the actual arg count
///
/// Anything else produces an Error response. Handler exceptions are caught
/// and converted to -ERR internal error (and logged) so a buggy handler
/// doesn't kill the client connection.
///
/// Why is the dispatcher its own class (not just a method on Program)?
/// - Testable in isolation — give it a CommandRegistry with fake handlers,
///   verify all the error paths without spinning up a TCP server
/// - The Program.cs entry point becomes simpler — just wire it up and call
///   dispatcher.Execute(parsed, store) inside the read loop
/// </summary>
public sealed class CommandDispatcher
{
    private readonly CommandRegistry _registry;
    private readonly Action<Exception>? _exceptionLogger;

    /// <param name="registry">The command lookup table.</param>
    /// <param name="exceptionLogger">
    /// Called when a handler throws. Server passes its Log function;
    /// tests can pass null to suppress.
    /// </param>
    public CommandDispatcher(CommandRegistry registry, Action<Exception>? exceptionLogger = null)
    {
        _registry = registry;
        _exceptionLogger = exceptionLogger;
    }

    /// <summary>
    /// Validates and executes one command. Always returns a valid RespValue
    /// (either the handler's response or an Error) — never throws.
    /// </summary>
    public RespValue Execute(RespValue incoming, ICacheStore store)
    {
        // Step 1: Top-level must be an Array
        if (incoming is not RespValue.Array array)
            return CommandErrors.ProtocolError("expected array");

        // Step 2: Array must be non-null and non-empty
        if (array.Items is null || array.Items.Length == 0)
            return CommandErrors.EmptyCommand();

        // Step 3: First element must be a BulkString with non-null data —
        // that's the command name as UTF-8 bytes
        if (array.Items[0] is not RespValue.BulkString { Data: { } nameBytes })
            return CommandErrors.InvalidFormat();

        var commandName = System.Text.Encoding.UTF8.GetString(nameBytes);

        // Step 4: Handler lookup (case-insensitive via the registry)
        if (!_registry.TryGet(commandName, out var handler) || handler is null)
            return CommandErrors.UnknownCommand(commandName);

        // Step 5: All other elements must also be BulkStrings with non-null data.
        // Build the Args array as we validate — single pass, no waste.
        var args = new ReadOnlyMemory<byte>[array.Items.Length];
        for (int i = 0; i < array.Items.Length; i++)
        {
            if (array.Items[i] is not RespValue.BulkString { Data: { } argBytes })
                return CommandErrors.InvalidFormat();
            args[i] = argBytes;
        }

        // Step 6: Arity validation — done here, not in handlers, so handler
        // code can trust args is the right shape
        if (!handler.Arity.Matches(args.Length))
            return CommandErrors.WrongArgCount(commandName);

        // Step 7: Execute under try/catch. A handler exception MUST NOT
        // propagate into the read loop — that would tear down the client
        // connection on a bug we can recover from.
        try
        {
            return handler.Execute(new CommandContext(store, args));
        }
        catch (Exception ex)
        {
            _exceptionLogger?.Invoke(ex);
            return CommandErrors.InternalError();
        }
    }
}
