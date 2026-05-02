using Driftworld.Api.Pagination;
using Driftworld.Core;
using Driftworld.Core.Aggregation;
using Driftworld.Core.Exceptions;
using Driftworld.Core.Rules;
using Driftworld.Data;
using Driftworld.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Driftworld.Api.Endpoints;

public static class WorldEndpoints
{
    public sealed record CycleDto(int Id, DateTime StartsAt, DateTime EndsAt, string Status);
    public sealed record StateDto(short Economy, short Environment, short Stability, int AsOfCycleId);
    public sealed record ActiveEventDto(string Type, int SinceCycleId);
    public sealed record CurrentResponse(CycleDto Cycle, StateDto State, IReadOnlyList<ActiveEventDto> ActiveEvents);

    public sealed record HistoryItem(int CycleId, short Economy, short Environment, short Stability, int Participants, DateTime CreatedAt);
    public sealed record HistoryResponse(IReadOnlyList<HistoryItem> Items);

    private const int HistoryDefaultLimit = 30;
    private const int HistoryMaxLimit = 365;

    public static RouteGroupBuilder MapWorldEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/world");
        group.MapGet("/current", GetCurrentAsync).WithName("GetCurrentWorld");
        group.MapGet("/history", GetHistoryAsync).WithName("GetWorldHistory");
        return root;
    }

    private static async Task<IResult> GetCurrentAsync(
        DriftworldDbContext db,
        IOptions<WorldOptions> world,
        CancellationToken ct)
    {
        var openCycle = await db.Cycles.FirstOrDefaultAsync(c => c.Status == CycleStatus.Open, ct)
            ?? throw new NoOpenCycleException();

        var latestState = await db.WorldStates
            .OrderByDescending(s => s.CycleId)
            .FirstAsync(ct);

        var stateValue = new WorldStateValue(
            latestState.Economy, latestState.Environment, latestState.Stability, latestState.Participants);

        var matching = RuleEvaluator.EvaluateMatching(stateValue, world.Value);
        var activeEvents = new List<ActiveEventDto>(matching.Count);
        foreach (var ruleName in matching)
        {
            var since = await ComputeSinceCycleIdAsync(db, world.Value.Rules[ruleName], latestState.CycleId, ct);
            activeEvents.Add(new ActiveEventDto(ruleName, since));
        }

        return Results.Ok(new CurrentResponse(
            new CycleDto(openCycle.Id, openCycle.StartsAt, openCycle.EndsAt, "open"),
            new StateDto(stateValue.Economy, stateValue.Environment, stateValue.Stability, latestState.CycleId),
            activeEvents));
    }

    private static async Task<int> ComputeSinceCycleIdAsync(
        DriftworldDbContext db, RuleOptions rule, int latestCycleId, CancellationToken ct)
    {
        // Walk closed world_states backward; the earliest contiguous cycle where the rule holds is `since`.
        // Bounded by HistoryMaxLimit to cap the worst case.
        var states = await db.WorldStates
            .Where(s => s.CycleId <= latestCycleId)
            .OrderByDescending(s => s.CycleId)
            .Take(HistoryMaxLimit)
            .Select(s => new { s.CycleId, s.Economy, s.Environment, s.Stability, s.Participants })
            .ToListAsync(ct);

        var since = latestCycleId;
        foreach (var s in states)
        {
            var v = new WorldStateValue(s.Economy, s.Environment, s.Stability, s.Participants);
            if (RuleEvaluator.IsRuleHolding(v, rule))
                since = s.CycleId;
            else
                break;
        }
        return since;
    }

    private static async Task<IResult> GetHistoryAsync(
        int? limit,
        DriftworldDbContext db,
        CancellationToken ct)
    {
        var n = LimitValidator.Validate(limit, HistoryDefaultLimit, HistoryMaxLimit);

        var items = await db.WorldStates
            .OrderByDescending(s => s.CycleId)
            .Take(n)
            .Select(s => new HistoryItem(s.CycleId, s.Economy, s.Environment, s.Stability, s.Participants, s.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(new HistoryResponse(items));
    }
}
