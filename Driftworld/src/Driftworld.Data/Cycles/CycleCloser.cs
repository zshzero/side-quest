using Driftworld.Core;
using Driftworld.Core.Aggregation;
using Driftworld.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Driftworld.Data.Cycles;

public static class CycleCloser
{
    public static async Task<CycleCloseResult> RunAsync(
        DriftworldDbContext db,
        WorldOptions world,
        TimeProvider clock,
        CancellationToken ct = default)
    {
        var closed = new List<int>();
        while (await CloseOneIfDueAsync(db, world, clock, ct) is { } closedCycleId)
            closed.Add(closedCycleId);
        return new CycleCloseResult(closed.Count, closed);
    }

    private static async Task<int?> CloseOneIfDueAsync(
        DriftworldDbContext db,
        WorldOptions world,
        TimeProvider clock,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var openCycle = await db.Cycles
            .FromSqlInterpolated($"SELECT * FROM cycles WHERE status = 'open' FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (openCycle is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (now < openCycle.EndsAt)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        var prev = await db.WorldStates
            .OrderByDescending(s => s.CycleId)
            .Select(s => new WorldStateValue(s.Economy, s.Environment, s.Stability, s.Participants))
            .FirstAsync(ct);

        var choices = await db.Decisions
            .Where(d => d.CycleId == openCycle.Id)
            .Select(d => d.Choice)
            .ToListAsync(ct);

        var next = WorldStateAggregator.AggregateAndApply(prev, choices, world);
        var closedAt = ClampClosedAt(now, openCycle.EndsAt);

        db.WorldStates.Add(new WorldState
        {
            CycleId = openCycle.Id,
            Economy = next.Economy,
            Environment = next.Environment,
            Stability = next.Stability,
            Participants = next.Participants,
            CreatedAt = closedAt,
        });

        openCycle.Status = CycleStatus.Closed;
        openCycle.ClosedAt = closedAt;

        db.Cycles.Add(new Cycle
        {
            StartsAt = openCycle.EndsAt,
            EndsAt = openCycle.EndsAt.AddHours(24),
            Status = CycleStatus.Open,
            ClosedAt = null,
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return openCycle.Id;
    }

    /// <summary>
    /// Pin closed_at to nominal close time (ends_at + 5min cron slack) so multi-day catch-up
    /// runs don't make history look like the cycle closed days late.
    /// </summary>
    private static DateTime ClampClosedAt(DateTime now, DateTime endsAt)
    {
        var nominal = endsAt.AddMinutes(5);
        return now < nominal ? now : nominal;
    }
}
