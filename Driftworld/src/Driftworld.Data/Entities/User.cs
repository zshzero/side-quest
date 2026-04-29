namespace Driftworld.Data.Entities;

public sealed class User
{
    public Guid Id { get; set; }
    public string? Handle { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}
