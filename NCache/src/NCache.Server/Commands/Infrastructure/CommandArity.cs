namespace NCache.Server.Commands.Infrastructure;

/// <summary>
/// Declares how many arguments a command accepts (the command name itself
/// counts as arg[0], so SET key value has arity 3).
///
/// This is a closed discriminated union so the dispatcher can validate arity
/// uniformly without knowing whether the command is fixed-arity (Exact) or
/// variadic (AtLeast). The Matches() method encapsulates the comparison.
///
/// Two variants cover everything Phase 2 needs:
/// - Exact(N)   — SET, GET, DBSIZE: rigid count
/// - AtLeast(N) — DEL, EXISTS: command name + 1+ keys
/// </summary>
public abstract record CommandArity
{
    private CommandArity() { }

    public abstract bool Matches(int argCount);

    /// <summary>
    /// Command requires exactly Count arguments (including the command name).
    /// </summary>
    public sealed record Exact(int Count) : CommandArity
    {
        public override bool Matches(int argCount) => argCount == Count;
    }

    /// <summary>
    /// Command requires at least Count arguments (including the command name).
    /// Used for variadic commands like DEL k1 [k2 ...] (Min = 2).
    /// </summary>
    public sealed record AtLeast(int Min) : CommandArity
    {
        public override bool Matches(int argCount) => argCount >= Min;
    }
}
