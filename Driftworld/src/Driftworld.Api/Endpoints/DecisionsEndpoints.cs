using Driftworld.Core;
using Driftworld.Core.Exceptions;
using Driftworld.Data;
using Driftworld.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Driftworld.Api.Endpoints;

public static class DecisionsEndpoints
{
    public sealed record CreateDecisionRequest(string Choice);
    public sealed record CreateDecisionResponse(Guid DecisionId, int CycleId);

    public static RouteGroupBuilder MapDecisionsEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/decisions");
        group.MapPost("/", CreateDecisionAsync).WithName("CreateDecision");
        return root;
    }

    private static async Task<IResult> CreateDecisionAsync(
        CreateDecisionRequest body,
        HttpContext http,
        DriftworldDbContext db,
        IOptions<WorldOptions> world,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = ExtractUserId(http);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnknownUserException(userId);

        if (string.IsNullOrWhiteSpace(body.Choice) || !world.Value.Choices.ContainsKey(body.Choice))
            throw new UnknownChoiceException(body.Choice ?? "", world.Value.Choices.Keys.ToArray());

        var openCycle = await db.Cycles.FirstOrDefaultAsync(c => c.Status == CycleStatus.Open, ct)
            ?? throw new NoOpenCycleException();

        var now = clock.GetUtcNow().UtcDateTime;

        var decision = new Decision
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CycleId = openCycle.Id,
            Choice = body.Choice,
            CreatedAt = now,
        };
        db.Decisions.Add(decision);
        user.LastSeenAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } pg)
        {
            throw pg.ConstraintName switch
            {
                "ux_decisions_user_cycle" => new DuplicateDecisionException(user.Id, openCycle.Id),
                _ => new InvalidOperationException(
                    $"Unexpected unique constraint violation: {pg.ConstraintName}", e),
            };
        }

        return Results.Created(
            $"/v1/decisions/{decision.Id}",
            new CreateDecisionResponse(decision.Id, openCycle.Id));
    }

    private static Guid ExtractUserId(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("X-User-Id", out var raw) || raw.Count == 0)
            throw new MissingUserIdException();

        var s = raw.ToString();
        if (string.IsNullOrWhiteSpace(s))
            throw new MissingUserIdException();

        if (!Guid.TryParse(s, out var userId))
            throw new MalformedUserIdException(s);

        return userId;
    }
}
