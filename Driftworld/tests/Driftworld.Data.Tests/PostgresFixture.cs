using Driftworld.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Driftworld.Data.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("driftworld")
        .WithUsername("driftworld")
        .WithPassword("driftworld")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply migrations once for this fixture's lifetime.
        await using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public DriftworldDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<DriftworldDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new DriftworldDbContext(opts);
    }

    /// <summary>Truncates all data tables; keeps the schema. Call between tests for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE events, decisions, world_states, cycles, users RESTART IDENTITY CASCADE");
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
