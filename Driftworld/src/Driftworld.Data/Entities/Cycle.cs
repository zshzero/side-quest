namespace Driftworld.Data.Entities;

public sealed class Cycle
{
    public int Id { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public CycleStatus Status { get; set; }
    public DateTime? ClosedAt { get; set; }
}
