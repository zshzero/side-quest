using Driftworld.Data;
using Driftworld.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Driftworld.Data.Tests;

[Collection(nameof(PostgresCollection))]
public class SchemaConstraintTests
{
    private readonly PostgresFixture _fx;

    public SchemaConstraintTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Migration_creates_all_5_tables()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var tables = await ctx.Database
            .SqlQueryRaw<string>("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename NOT LIKE '\\_\\_%'")
            .ToListAsync();

        tables.Should().BeEquivalentTo(new[] { "users", "cycles", "decisions", "world_states", "events" });
    }

    [Fact]
    public async Task Partial_unique_index_rejects_two_open_cycles()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        ctx.Cycles.Add(new Cycle
        {
            StartsAt = DateTime.UtcNow,
            EndsAt = DateTime.UtcNow.AddHours(24),
            Status = CycleStatus.Open,
        });
        await ctx.SaveChangesAsync();

        ctx.Cycles.Add(new Cycle
        {
            StartsAt = DateTime.UtcNow.AddHours(24),
            EndsAt = DateTime.UtcNow.AddHours(48),
            Status = CycleStatus.Open,
        });

        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Cycle_status_check_rejects_unknown_value()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO cycles (starts_at, ends_at, status) VALUES (now(), now() + interval '24 hours', 'archived')");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514"); // check_violation
    }

    [Theory]
    [InlineData("economy")]
    [InlineData("environment")]
    [InlineData("stability")]
    public async Task World_state_variable_range_check_rejects_above_100(string column)
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var cycle = new Cycle { StartsAt = DateTime.UtcNow.AddHours(-24), EndsAt = DateTime.UtcNow, Status = CycleStatus.Closed, ClosedAt = DateTime.UtcNow };
        ctx.Cycles.Add(cycle);
        await ctx.SaveChangesAsync();

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            $"INSERT INTO world_states (cycle_id, economy, environment, stability, participants, created_at) " +
            $"VALUES ({cycle.Id}, " +
            $"  {(column == "economy" ? 101 : 50)}, " +
            $"  {(column == "environment" ? 101 : 50)}, " +
            $"  {(column == "stability" ? 101 : 50)}, " +
            $"  0, now())");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task World_state_participants_check_rejects_negative()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var cycle = new Cycle { StartsAt = DateTime.UtcNow.AddHours(-24), EndsAt = DateTime.UtcNow, Status = CycleStatus.Closed, ClosedAt = DateTime.UtcNow };
        ctx.Cycles.Add(cycle);
        await ctx.SaveChangesAsync();

        var act = async () => await ctx.Database.ExecuteSqlRawAsync(
            $"INSERT INTO world_states (cycle_id, economy, environment, stability, participants, created_at) " +
            $"VALUES ({cycle.Id}, 50, 50, 50, -1, now())");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Filtered_handle_index_allows_multiple_nulls_but_rejects_duplicates()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        ctx.Users.Add(new User { Id = Guid.NewGuid(), Handle = null, CreatedAt = DateTime.UtcNow });
        ctx.Users.Add(new User { Id = Guid.NewGuid(), Handle = null, CreatedAt = DateTime.UtcNow });
        ctx.Users.Add(new User { Id = Guid.NewGuid(), Handle = "ada", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        ctx.Users.Add(new User { Id = Guid.NewGuid(), Handle = "ada", CreatedAt = DateTime.UtcNow });
        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Composite_unique_decisions_user_cycle_rejects_duplicates()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var user = new User { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
        var cycle = new Cycle { StartsAt = DateTime.UtcNow, EndsAt = DateTime.UtcNow.AddHours(24), Status = CycleStatus.Open };
        ctx.Users.Add(user);
        ctx.Cycles.Add(cycle);
        await ctx.SaveChangesAsync();

        ctx.Decisions.Add(new Decision { Id = Guid.NewGuid(), UserId = user.Id, CycleId = cycle.Id, Choice = "build", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        ctx.Decisions.Add(new Decision { Id = Guid.NewGuid(), UserId = user.Id, CycleId = cycle.Id, Choice = "preserve", CreatedAt = DateTime.UtcNow });
        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Composite_unique_events_cycle_type_rejects_duplicates()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var cycle = new Cycle { StartsAt = DateTime.UtcNow.AddHours(-24), EndsAt = DateTime.UtcNow, Status = CycleStatus.Closed, ClosedAt = DateTime.UtcNow };
        ctx.Cycles.Add(cycle);
        await ctx.SaveChangesAsync();

        ctx.Events.Add(new Event { Id = Guid.NewGuid(), CycleId = cycle.Id, Type = "recession", Payload = "{}", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        ctx.Events.Add(new Event { Id = Guid.NewGuid(), CycleId = cycle.Id, Type = "recession", Payload = "{\"a\":1}", CreatedAt = DateTime.UtcNow });
        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }
}
