using NCache.Protocol;

namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// The contract every command implements.
///
/// Two members — that's the whole interface. Each command is a small,
/// self-contained class. Adding a new command means writing a new class and
/// registering it; nothing in the dispatcher or other commands needs to change.
/// (Open/Closed Principle — the textbook example.)
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Declares the expected argument count. Read by the dispatcher BEFORE
    /// calling Execute, so handlers can assume args are well-shaped.
    /// </summary>
    CommandArity Arity { get; }

    /// <summary>
    /// Runs the command and returns the response to send to the client.
    ///
    /// Synchronous: Phase 2 handlers only touch in-memory data. Phase 5
    /// (persistence) and Phase 6 (pub/sub) may force this to Task&lt;RespValue&gt;
    /// later — we'll cross that bridge then.
    /// </summary>
    RespValue Execute(CommandContext ctx);
}
