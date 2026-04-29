using Driftworld.Data.Entities;
using Driftworld.Data.Seeding;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Driftworld.Data.Tests;

[Collection(nameof(PostgresCollection))]
public class GenesisSeederTests
{
    private readonly PostgresFixture _fx;
    private static readonly TimeProvider FixedClock = new FixedTimeProvider(
        new DateTimeOffset(2026, 4, 28, 14, 30, 0, TimeSpan.Zero));

    public GenesisSeederTests(PostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task First_seed_creates_genesis_and_first_open_cycle()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var result = await GenesisSeeder.EnsureSeededAsync(ctx, FixedClock);

        result.Applied.Should().BeTrue();
        result.T0.Should().Be(new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));

        var cycles = await ctx.Cycles.OrderBy(c => c.Id).AsNoTracking().ToListAsync();
        cycles.Should().HaveCount(2);
        cycles[0].Status.Should().Be(CycleStatus.Closed);
        cycles[0].StartsAt.Should().Be(new DateTime(2026, 4, 27, 0, 0, 0, DateTimeKind.Utc));
        cycles[0].EndsAt.Should().Be(new DateTime(2026, 4, 28, 0, 0, 0, DateTimeKind.Utc));
        cycles[1].Status.Should().Be(CycleStatus.Open);
        cycles[1].EndsAt.Should().Be(new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc));

        var states = await ctx.WorldStates.AsNoTracking().ToListAsync();
        states.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                CycleId = cycles[0].Id,
                Economy = (short)50,
                Environment = (short)50,
                Stability = (short)50,
                Participants = 0,
            });
    }

    [Fact]
    public async Task Second_seed_is_a_noop()
    {
        await _fx.ResetAsync();
        await using var ctx = _fx.CreateContext();

        var first = await GenesisSeeder.EnsureSeededAsync(ctx, FixedClock);
        var second = await GenesisSeeder.EnsureSeededAsync(ctx, FixedClock);

        first.Applied.Should().BeTrue();
        second.Applied.Should().BeFalse();
        second.OpenCycleId.Should().Be(first.OpenCycleId);

        (await ctx.Cycles.CountAsync()).Should().Be(2);
        (await ctx.WorldStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_seeders_only_one_applies()
    {
        await _fx.ResetAsync();

        // 5 concurrent seeders racing — the advisory lock + existence check
        // inside the same transaction must serialize them so only one wins.
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await using var ctx = _fx.CreateContext();
            return await GenesisSeeder.EnsureSeededAsync(ctx, FixedClock);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Applied).Should().Be(1);
        results.Count(r => !r.Applied).Should().Be(4);

        await using var verifyCtx = _fx.CreateContext();
        (await verifyCtx.Cycles.CountAsync()).Should().Be(2);
        (await verifyCtx.WorldStates.CountAsync()).Should().Be(1);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
