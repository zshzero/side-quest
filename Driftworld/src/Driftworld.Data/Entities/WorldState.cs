namespace Driftworld.Data.Entities;

public sealed class WorldState
{
    public int CycleId { get; set; }
    public short Economy { get; set; }
    public short Environment { get; set; }
    public short Stability { get; set; }
    public int Participants { get; set; }
    public DateTime CreatedAt { get; set; }

    public Cycle? Cycle { get; set; }
}
