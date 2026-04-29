namespace Driftworld.Data.Entities;

public sealed class Decision
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int CycleId { get; set; }
    public string Choice { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public User? User { get; set; }
    public Cycle? Cycle { get; set; }
}
