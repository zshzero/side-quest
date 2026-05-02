using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Entities;
using Driftworld.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Driftworld.Worker.Tests;

public sealed class WorkerPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("driftworld")
        .WithUsername("driftworld")
        .WithPassword("driftworld")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public AdvanceableClock Clock { get; } = new(
        new DateTimeOffset(2026, 4, 28, 14, 30, 0, TimeSpan.Zero));

    public WorldOptions World { get; } = new()
    {
        K = 2,
        Choices =
        {
            ["build"]     = new ChoiceDelta { Economy =  3, Environment = -2, Stability =  0 },
            ["preserve"]  = new ChoiceDelta { Economy = -1, Environment =  3, Stability =  0 },
            ["stabilize"] = new ChoiceDelta { Economy = -1, Environment =  0, Stability =  3 },
        },
    };

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public DriftworldDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<DriftworldDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new DriftworldDbContext(opts);
    }

    public async Task ResetAndSeedAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE events, decisions, world_states, cycles, users RESTART IDENTITY CASCADE");
        await GenesisSeeder.EnsureSeededAsync(ctx, Clock);
    }

    /// <summary>Backdate the open cycle so its <c>ends_at</c> is in the past.</summary>
    public async Task BackdateOpenCycleAsync(TimeSpan endsAtBefore)
    {
        await using var ctx = CreateContext();
        var open = await ctx.Cycles.SingleAsync(c => c.Status == CycleStatus.Open);
        var now = Clock.GetUtcNow().UtcDateTime;
        open.EndsAt = now - endsAtBefore;
        open.StartsAt = open.EndsAt.AddHours(-24);
        await ctx.SaveChangesAsync();
    }

    public async Task SeedDecisionsForOpenCycleAsync(IEnumerable<string> choices)
    {
        await using var ctx = CreateContext();
        var open = await ctx.Cycles.SingleAsync(c => c.Status == CycleStatus.Open);

        foreach (var choice in choices)
        {
            var user = new User { Id = Guid.NewGuid(), CreatedAt = Clock.GetUtcNow().UtcDateTime };
            ctx.Users.Add(user);
            ctx.Decisions.Add(new Decision
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                CycleId = open.Id,
                Choice = choice,
                CreatedAt = Clock.GetUtcNow().UtcDateTime,
            });
        }

        await ctx.SaveChangesAsync();
    }
}

public sealed class AdvanceableClock : TimeProvider
{
    private DateTimeOffset _now;
    public AdvanceableClock(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void SetTo(DateTimeOffset moment) => _now = moment;
}

[CollectionDefinition(nameof(WorkerPostgresCollection))]
public sealed class WorkerPostgresCollection : ICollectionFixture<WorkerPostgresFixture>
{
}
