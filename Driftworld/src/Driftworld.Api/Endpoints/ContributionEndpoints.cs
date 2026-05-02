using Driftworld.Core.Exceptions;
using Driftworld.Data;
using Microsoft.EntityFrameworkCore;

namespace Driftworld.Api.Endpoints;

public static class ContributionEndpoints
{
    public sealed record AlignmentDto(int WithMajorityPct);

    public sealed record ContributionResponse(
        Guid UserId,
        int TotalDecisions,
        IDictionary<string, int> ByChoice,
        AlignmentDto Alignment);

    public static RouteGroupBuilder MapContributionEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/users");
        group.MapGet("/{id:guid}/contribution", GetContributionAsync).WithName("GetUserContribution");
        return root;
    }

    private static async Task<IResult> GetContributionAsync(
        Guid id,
        DriftworldDbContext db,
        CancellationToken ct)
    {
        var userExists = await db.Users.AnyAsync(u => u.Id == id, ct);
        if (!userExists)
            throw new UnknownUserException(id);

        var userDecisions = await db.Decisions
            .Where(d => d.UserId == id)
            .Select(d => new { d.CycleId, d.Choice })
            .ToListAsync(ct);

        if (userDecisions.Count == 0)
        {
            return Results.Ok(new ContributionResponse(
                UserId: id,
                TotalDecisions: 0,
                ByChoice: new Dictionary<string, int>(),
                Alignment: new AlignmentDto(WithMajorityPct: 0)));
        }

        var byChoice = userDecisions
            .GroupBy(d => d.Choice, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Pull all decisions for the cycles this user participated in (one round trip).
        var participatedCycleIds = userDecisions.Select(d => d.CycleId).Distinct().ToList();
        var cycleChoiceCounts = await db.Decisions
            .Where(d => participatedCycleIds.Contains(d.CycleId))
            .GroupBy(d => new { d.CycleId, d.Choice })
            .Select(g => new { g.Key.CycleId, g.Key.Choice, Count = g.Count() })
            .ToListAsync(ct);

        // For each cycle, compute the modal choice (alphabetical tiebreak).
        var modalByCycle = cycleChoiceCounts
            .GroupBy(x => x.CycleId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).ThenBy(x => x.Choice, StringComparer.Ordinal).First().Choice);

        var matches = userDecisions.Count(d =>
            modalByCycle.TryGetValue(d.CycleId, out var modal)
            && string.Equals(modal, d.Choice, StringComparison.OrdinalIgnoreCase));

        var alignment = (int)Math.Round((decimal)matches * 100m / userDecisions.Count, MidpointRounding.AwayFromZero);

        return Results.Ok(new ContributionResponse(
            UserId: id,
            TotalDecisions: userDecisions.Count,
            ByChoice: byChoice,
            Alignment: new AlignmentDto(alignment)));
    }
}
