using NCache.Protocol;

namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// Central factory for error responses.
///
/// Why centralise? Three reasons:
/// 1. Consistency — every "wrong arg count" message reads identically
/// 2. Redis compatibility — wording matches real Redis (important for redis-cli
///    integration); fixes happen in one place
/// 3. Test stability — assertions reference the same source of truth
///
/// Wording note: Redis errors conventionally start with an uppercase ERR-style
/// token (ERR, WRONGTYPE, NOAUTH, etc.) followed by a space and a human
/// message. We preserve that convention even when adding our own errors.
/// </summary>
public static class CommandErrors
{
    public static RespValue.Error UnknownCommand(string name)
        => new($"ERR unknown command '{name}'");

    public static RespValue.Error WrongArgCount(string name)
        => new($"ERR wrong number of arguments for '{name}' command");

    public static RespValue.Error ProtocolError(string detail)
        => new($"ERR Protocol error: {detail}");

    public static RespValue.Error EmptyCommand()
        => new("ERR empty command");

    public static RespValue.Error InvalidFormat()
        => new("ERR invalid command format");

    public static RespValue.Error InternalError()
        => new("ERR internal error");
}
