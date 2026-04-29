namespace Driftworld.Core;

public sealed class ChoiceDelta
{
    public short Economy { get; init; }
    public short Environment { get; init; }
    public short Stability { get; init; }

    public short For(WorldVariable v) => v switch
    {
        WorldVariable.Economy => Economy,
        WorldVariable.Environment => Environment,
        WorldVariable.Stability => Stability,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };
}
