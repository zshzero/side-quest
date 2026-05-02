namespace Driftworld.Core.Aggregation;

public sealed record WorldStateValue(
    short Economy,
    short Environment,
    short Stability,
    int Participants)
{
    public short GetVariable(WorldVariable v) => v switch
    {
        WorldVariable.Economy => Economy,
        WorldVariable.Environment => Environment,
        WorldVariable.Stability => Stability,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null),
    };
}
