namespace Driftworld.Data.Cycles;

public sealed record CycleCloseResult(int CyclesClosed, IReadOnlyList<int> ClosedCycleIds);
