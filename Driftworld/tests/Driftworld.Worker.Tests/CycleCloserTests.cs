using Driftworld.Data.Cycles;
using Driftworld.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Driftworld.Worker.Tests;

[Collection(nameof(WorkerPostgresCollection))]
public class CycleCloserTests
{
    private readonly WorkerPostgresFixture _fx;

    public CycleCloserTests(WorkerPostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Worker_does_nothing_if_no_cycle_is_due()
    {
        await _fx.ResetAndSeedAsync();
        // Genesis seed leaves cycle 2 open with ends_at=tomorrow; clock is at "now".
        await using var ctx = _fx.CreateContext();
        var result = await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);

        result.CyclesClosed.Should().Be(0);

        // Still cycle 1 closed + cycle 2 open + 1 world_state.
        (await ctx.Cycles.CountAsync()).Should().Be(2);
        (await ctx.WorldStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Single_cycle_close_with_5_decisions_matches_hand_calc()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromMinutes(10));
        await _fx.SeedDecisionsForOpenCycleAsync(new[] { "build", "build", "preserve", "preserve", "stabilize" });

        await using var ctx = _fx.CreateContext();
        var result = await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);

        result.CyclesClosed.Should().Be(1);
        result.ClosedCycleIds.Single().Should().Be(2);

        // Hand-calc per WorldStateAggregatorTests.Mixed_decisions_match_hand_calc:
        //   sum_economy = 2*3 + 2*-1 + 1*-1 = 3, mean = 0.6, raw = 50 + 1.2 = 51.2 → 51
        //   sum_environment = 2*-2 + 2*3 = 2, mean = 0.4, raw = 50 + 0.8 = 50.8 → 51
        //   sum_stability = 1*3 = 3, mean = 0.6, raw = 50 + 1.2 = 51.2 → 51
        var newState = await ctx.WorldStates.SingleAsync(s => s.CycleId == 2);
        newState.Economy.Should().Be(51);
        newState.Environment.Should().Be(51);
        newState.Stability.Should().Be(51);
        newState.Participants.Should().Be(5);

        // Successor cycle exists.
        var cycles = await ctx.Cycles.OrderBy(c => c.Id).ToListAsync();
        cycles.Should().HaveCount(3);
        cycles[1].Status.Should().Be(CycleStatus.Closed);
        cycles[2].Status.Should().Be(CycleStatus.Open);
        cycles[2].StartsAt.Should().Be(cycles[1].EndsAt);
    }

    [Fact]
    public async Task Re_running_with_no_new_decisions_is_a_noop()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromMinutes(10));
        await _fx.SeedDecisionsForOpenCycleAsync(new[] { "build" });

        await using var ctx1 = _fx.CreateContext();
        var first = await CycleCloser.RunAsync(ctx1, _fx.World, _fx.Clock);
        first.CyclesClosed.Should().Be(1);

        await using var ctx2 = _fx.CreateContext();
        var second = await CycleCloser.RunAsync(ctx2, _fx.World, _fx.Clock);
        second.CyclesClosed.Should().Be(0);

        await using var verify = _fx.CreateContext();
        (await verify.WorldStates.CountAsync()).Should().Be(2); // genesis + 1 new
        (await verify.Cycles.CountAsync()).Should().Be(3);     // genesis-1 + closed-2 + new-open-3
    }

    [Fact]
    public async Task Empty_cycle_carries_state_forward_unchanged()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromMinutes(10));
        // No decisions added.

        await using var ctx = _fx.CreateContext();
        var result = await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);
        result.CyclesClosed.Should().Be(1);

        var states = await ctx.WorldStates.OrderBy(s => s.CycleId).ToListAsync();
        states.Should().HaveCount(2);
        states[1].Economy.Should().Be(states[0].Economy);
        states[1].Environment.Should().Be(states[0].Environment);
        states[1].Stability.Should().Be(states[0].Stability);
        states[1].Participants.Should().Be(0);
    }

    [Fact]
    public async Task Multi_day_catch_up_closes_three_cycles_in_one_invocation()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromHours(60)); // 2.5 days — closes cycles 2, 3, 4 cleanly
        // Backdating gives one open cycle whose ends_at is 3 days in the past.
        // Each closed iteration opens a successor 24h later, so the loop closes 3 cycles total
        // before the new "open" cycle's ends_at lands in the future.
        await _fx.SeedDecisionsForOpenCycleAsync(new[] { "build" });

        await using var ctx = _fx.CreateContext();
        var result = await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);

        result.CyclesClosed.Should().Be(3);
        result.ClosedCycleIds.Should().Equal(2, 3, 4);

        var cycles = await ctx.Cycles.OrderBy(c => c.Id).ToListAsync();
        cycles.Should().HaveCount(5);
        cycles.Take(4).All(c => c.Status == CycleStatus.Closed).Should().BeTrue();
        cycles[4].Status.Should().Be(CycleStatus.Open);

        // Cycles 3 and 4 had no decisions → state copies forward from cycle 2's result.
        var states = await ctx.WorldStates.OrderBy(s => s.CycleId).ToListAsync();
        states.Should().HaveCount(4); // genesis (1) + 3 new (2,3,4)
        states[1].Participants.Should().Be(1);
        states[2].Participants.Should().Be(0);
        states[3].Participants.Should().Be(0);
        states[2].Economy.Should().Be(states[1].Economy);
        states[3].Economy.Should().Be(states[2].Economy);
    }

    [Fact]
    public async Task Closed_at_is_pinned_to_nominal_close_time_during_catch_up()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromHours(60)); // 2.5 days — closes cycles 2, 3, 4 cleanly

        await using var ctx = _fx.CreateContext();
        await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);

        var closed = await ctx.Cycles
            .Where(c => c.Status == CycleStatus.Closed && c.Id > 1)
            .OrderBy(c => c.Id)
            .ToListAsync();

        // Each closed_at should equal that cycle's ends_at + 5min — NOT wall-clock now.
        foreach (var c in closed)
            c.ClosedAt.Should().Be(c.EndsAt.AddMinutes(5));
    }

    [Fact]
    public async Task Concurrent_workers_close_each_due_cycle_exactly_once()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.BackdateOpenCycleAsync(endsAtBefore: TimeSpan.FromMinutes(10));
        await _fx.SeedDecisionsForOpenCycleAsync(new[] { "build" });

        // 5 workers race; at most one closes the single due cycle, the rest see status='closed' and exit.
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await using var ctx = _fx.CreateContext();
            return await CycleCloser.RunAsync(ctx, _fx.World, _fx.Clock);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        results.Sum(r => r.CyclesClosed).Should().Be(1);

        await using var verify = _fx.CreateContext();
        (await verify.Cycles.CountAsync(c => c.Status == CycleStatus.Closed)).Should().Be(2); // genesis + just-closed
        (await verify.Cycles.CountAsync(c => c.Status == CycleStatus.Open)).Should().Be(1);
    }
}
