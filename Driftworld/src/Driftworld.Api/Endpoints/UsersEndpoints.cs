using Driftworld.Core.Exceptions;
using Driftworld.Data;
using Driftworld.Data.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Driftworld.Api.Endpoints;

public static class UsersEndpoints
{
    public sealed record CreateUserRequest(string? Handle);
    public sealed record CreateUserResponse(Guid UserId, string? Handle);

    public static RouteGroupBuilder MapUsersEndpoints(this RouteGroupBuilder root)
    {
        var group = root.MapGroup("/users");
        group.MapPost("/", CreateUserAsync).WithName("CreateUser");
        return root;
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest body,
        DriftworldDbContext db,
        IValidator<string> handleValidator,
        TimeProvider clock,
        CancellationToken ct)
    {
        var handle = string.IsNullOrWhiteSpace(body.Handle) ? null : body.Handle.Trim();

        if (handle is not null)
        {
            var validation = await handleValidator.ValidateAsync(handle, ct);
            if (!validation.IsValid)
            {
                var reasons = validation.Errors.Select(e => e.ErrorMessage).ToArray();
                throw new InvalidHandleException(handle, reasons);
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Handle = handle,
            CreatedAt = now,
            LastSeenAt = now,
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" } pg)
        {
            throw pg.ConstraintName switch
            {
                "ix_users_handle" => new DuplicateHandleException(handle!),
                _ => new InvalidOperationException(
                    $"Unexpected unique constraint violation: {pg.ConstraintName}", e),
            };
        }

        return Results.Created(
            $"/v1/users/{user.Id}",
            new CreateUserResponse(user.Id, user.Handle));
    }
}
