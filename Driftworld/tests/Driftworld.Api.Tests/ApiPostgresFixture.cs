using Driftworld.Data;
using Driftworld.Data.Entities;
using Driftworld.Data.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Driftworld.Api.Tests;

public sealed class ApiPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("driftworld")
        .WithUsername("driftworld")
        .WithPassword("driftworld")
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString => _container.GetConnectionString();

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialized.");

    public TimeProvider Clock { get; } = new FixedClock(
        new DateTimeOffset(2026, 4, 28, 14, 30, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Driftworld"] = ConnectionString,
                    ["Driftworld:World:K"] = "2",
                    ["Driftworld:World:Choices:build:Economy"] = "3",
                    ["Driftworld:World:Choices:build:Environment"] = "-2",
                    ["Driftworld:World:Choices:build:Stability"] = "0",
                    ["Driftworld:World:Choices:preserve:Economy"] = "-1",
                    ["Driftworld:World:Choices:preserve:Environment"] = "3",
                    ["Driftworld:World:Choices:preserve:Stability"] = "0",
                    ["Driftworld:World:Choices:stabilize:Economy"] = "-1",
                    ["Driftworld:World:Choices:stabilize:Environment"] = "0",
                    ["Driftworld:World:Choices:stabilize:Stability"] = "3",
                    ["Driftworld:World:Rules:recession:Variable"] = "Economy",
                    ["Driftworld:World:Rules:recession:Op"] = "Lt",
                    ["Driftworld:World:Rules:recession:Threshold"] = "20",
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Clock);
            });
        });

        // Materialize schema once.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Truncate all data + reseed genesis. Call between tests for isolation.</summary>
    public async Task ResetAndSeedAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE events, decisions, world_states, cycles, users RESTART IDENTITY CASCADE");

        await GenesisSeeder.EnsureSeededAsync(db, Clock);
    }

    public async Task<int> CloseOpenCycleAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
        var open = await db.Cycles.SingleAsync(c => c.Status == CycleStatus.Open);
        open.Status = CycleStatus.Closed;
        open.ClosedAt = Clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return open.Id;
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}

[CollectionDefinition(nameof(ApiPostgresCollection))]
public sealed class ApiPostgresCollection : ICollectionFixture<ApiPostgresFixture>
{
}
