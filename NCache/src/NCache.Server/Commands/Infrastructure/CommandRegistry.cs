namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// Maps command names to their handlers. Case-insensitive lookup so
/// "set", "SET", "Set" all resolve to the same handler (Redis behavior).
///
/// StringComparer.OrdinalIgnoreCase is the right choice here:
/// - Ordinal: byte-for-byte comparison, no culture surprises (e.g., Turkish I)
/// - IgnoreCase: ASCII case folding only — fine because command names are ASCII
///
/// Note: keys in CacheStore use StringComparer.Ordinal (case-sensitive).
/// Commands case-insensitive, keys case-sensitive — matches Redis exactly.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, ICommandHandler> _handlers
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a handler under the given command name. Overwrites silently
    /// if the name already exists — useful for tests; harmless in production
    /// where each name is registered once at startup.
    /// </summary>
    public void Register(string name, ICommandHandler handler)
    {
        _handlers[name] = handler;
    }

    /// <summary>
    /// Looks up a handler by name. Returns false if no command is registered
    /// under that name (the dispatcher converts this to "unknown command").
    /// </summary>
    public bool TryGet(string name, out ICommandHandler? handler)
    {
        return _handlers.TryGetValue(name, out handler);
    }
}
