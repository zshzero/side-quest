using Driftworld.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Driftworld.Data.Seeding;

public static class GenesisSeeder
{
    // Arbitrary 64-bit constant identifying the genesis-seed critical section.
    // Chosen randomly; the only requirement is that no other code uses the same key.
    private const long AdvisoryLockKey = 0x6472696674776c64L; // 'driftwld'

    public sealed record SeedResult(bool Applied, DateTime T0, int OpenCycleId);

    public static async Task<SeedResult> EnsureSeededAsync(
        DriftworldDbContext db,
        TimeProvider clock,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Block any concurrent seeder until this transaction ends.
        // Released automatically on COMMIT or ROLLBACK.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey },
            ct);

        var existingOpen = await db.Cycles
            .Where(c => c.Status == CycleStatus.Open)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);

        if (existingOpen is not null)
        {
            await tx.CommitAsync(ct);
            return new SeedResult(Applied: false, T0: existingOpen.StartsAt, OpenCycleId: existingOpen.Id);
        }

        var t0 = TruncateToUtcMidnight(clock.GetUtcNow().UtcDateTime);

        var genesisCycle = new Cycle
        {
            StartsAt = t0.AddHours(-24),
            EndsAt = t0,
            Status = CycleStatus.Closed,
            ClosedAt = t0,
        };

        var firstOpenCycle = new Cycle
        {
            StartsAt = t0,
            EndsAt = t0.AddHours(24),
            Status = CycleStatus.Open,
            ClosedAt = null,
        };

        db.Cycles.Add(genesisCycle);
        await db.SaveChangesAsync(ct);

        db.Cycles.Add(firstOpenCycle);
        await db.SaveChangesAsync(ct);

        db.WorldStates.Add(new WorldState
        {
            CycleId = genesisCycle.Id,
            Economy = 50,
            Environment = 50,
            Stability = 50,
            Participants = 0,
            CreatedAt = t0,
        });
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        return new SeedResult(Applied: true, T0: t0, OpenCycleId: firstOpenCycle.Id);
    }

    private static DateTime TruncateToUtcMidnight(DateTime utc) =>
        DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
}
