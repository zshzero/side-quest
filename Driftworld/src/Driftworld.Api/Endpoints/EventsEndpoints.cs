using System.Text.Json;
using Driftworld.Api.Pagination;
using Driftworld.Core.Exceptions;
using Driftworld.Data;
using Microsoft.EntityFrameworkCore;

namespace Driftworld.Api.Endpoints;

public static class EventsEndpoints
{
    public sealed record EventItem(int CycleId, string Type, JsonElement Payload, DateTime CreatedAt);
    public sealed record EventsResponse(IReadOnlyList<EventItem> Items);

    private const int DefaultLimit = 30;
    private const int MaxLimit = 200;

    public static RouteGroupBuilder MapEventsEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/events");
        group.MapGet("/", GetEventsAsync).WithName("GetEvents");
        return root;
    }

    private static async Task<IResult> GetEventsAsync(
        int? cycle_id,
        int? limit,
        DriftworldDbContext db,
        CancellationToken ct)
    {
        if (cycle_id is not null && limit is not null)
            throw new ConflictingFiltersException("cycle_id", "limit");

        IQueryable<Data.Entities.Event> query = db.Events;

        if (cycle_id is not null)
        {
            query = query.Where(e => e.CycleId == cycle_id.Value)
                         .OrderBy(e => e.Type);
        }
        else
        {
            var n = LimitValidator.Validate(limit, DefaultLimit, MaxLimit);
            query = query.OrderByDescending(e => e.CycleId).ThenBy(e => e.Type).Take(n);
        }

        var rows = await query
            .Select(e => new { e.CycleId, e.Type, e.Payload, e.CreatedAt })
            .ToListAsync(ct);

        var items = rows.Select(r => new EventItem(
            r.CycleId,
            r.Type,
            JsonSerializer.Deserialize<JsonElement>(r.Payload),
            r.CreatedAt)).ToList();

        return Results.Ok(new EventsResponse(items));
    }
}
