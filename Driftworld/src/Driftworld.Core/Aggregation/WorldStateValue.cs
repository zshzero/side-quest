namespace Driftworld.Core.Aggregation;

public sealed record WorldStateValue(
    short Economy,
    short Environment,
    short Stability,
    int Participants);
