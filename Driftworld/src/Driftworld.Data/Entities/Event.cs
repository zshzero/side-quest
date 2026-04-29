namespace Driftworld.Data.Entities;

public sealed class Event
{
    public Guid Id { get; set; }
    public int CycleId { get; set; }
    public string Type { get; set; } = "";
    public required string Payload { get; set; }
    public DateTime CreatedAt { get; set; }

    public Cycle? Cycle { get; set; }
}
