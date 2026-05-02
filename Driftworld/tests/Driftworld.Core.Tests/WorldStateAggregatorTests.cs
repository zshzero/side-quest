using Driftworld.Core;
using Driftworld.Core.Aggregation;
using Driftworld.Core.Exceptions;
using FluentAssertions;
using Xunit;

namespace Driftworld.Core.Tests;

public class WorldStateAggregatorTests
{
    private static WorldOptions World(decimal k = 2m) => new()
    {
        K = k,
        Choices =
        {
            ["build"]     = new ChoiceDelta { Economy =  3, Environment = -2, Stability =  0 },
            ["preserve"]  = new ChoiceDelta { Economy = -1, Environment =  3, Stability =  0 },
            ["stabilize"] = new ChoiceDelta { Economy = -1, Environment =  0, Stability =  3 },
        },
    };

    private static readonly WorldStateValue Neutral = new(50, 50, 50, 0);

    [Fact]
    public void Empty_cycle_copies_state_forward_with_zero_participants()
    {
        var prev = new WorldStateValue(53, 47, 50, 128);
        var result = WorldStateAggregator.AggregateAndApply(prev, Array.Empty<string>(), World());

        result.Economy.Should().Be(53);
        result.Environment.Should().Be(47);
        result.Stability.Should().Be(50);
        result.Participants.Should().Be(0);
    }

    [Fact]
    public void All_build_drives_economy_up_and_environment_down()
    {
        // mean_delta_economy     = +3, raw = 50 + 2*3  = 56
        // mean_delta_environment = -2, raw = 50 + 2*-2 = 46
        // mean_delta_stability   =  0, raw = 50
        var result = WorldStateAggregator.AggregateAndApply(
            Neutral, new[] { "build", "build", "build", "build", "build" }, World());

        result.Economy.Should().Be(56);
        result.Environment.Should().Be(46);
        result.Stability.Should().Be(50);
        result.Participants.Should().Be(5);
    }

    [Fact]
    public void Mixed_decisions_match_hand_calc()
    {
        // 2 build + 2 preserve + 1 stabilize, K=2:
        //   sum_economy     = 2*3 + 2*-1 + 1*-1 = 3       mean = 0.6   raw = 50 + 1.2 = 51.2 → 51
        //   sum_environment = 2*-2 + 2*3 + 1*0 = 2        mean = 0.4   raw = 50 + 0.8 = 50.8 → 51
        //   sum_stability   = 2*0 + 2*0 + 1*3 = 3         mean = 0.6   raw = 50 + 1.2 = 51.2 → 51
        var result = WorldStateAggregator.AggregateAndApply(
            Neutral, new[] { "build", "build", "preserve", "preserve", "stabilize" }, World());

        result.Economy.Should().Be(51);
        result.Environment.Should().Be(51);
        result.Stability.Should().Be(51);
        result.Participants.Should().Be(5);
    }

    [Fact]
    public void Positive_midpoint_rounds_away_from_zero()
    {
        // Two choices with sum_economy = 1, n=2 → mean = 0.5, raw = 50 + 2*0.5 = 51.0 (no midpoint there).
        // Construct an exact +0.5 midpoint: sum=1, n=4, K=2 → mean=0.25, raw=50+0.5=50.5 → 51.
        var world = World();
        world.Choices.Clear();
        world.Choices["plus_one_econ"] = new ChoiceDelta { Economy = 1, Environment = 0, Stability = 0 };
        world.Choices["zero"] = new ChoiceDelta { Economy = 0, Environment = 0, Stability = 0 };

        var result = WorldStateAggregator.AggregateAndApply(
            Neutral, new[] { "plus_one_econ", "zero", "zero", "zero" }, world);

        result.Economy.Should().Be(51); // 50.5 rounds to 51 (away from zero)
    }

    [Fact]
    public void Negative_midpoint_rounds_away_from_zero()
    {
        // sum=-1, n=4, K=2 → mean=-0.25, raw=50-0.5=49.5 → 49 (away from zero, downward toward -∞ from prev).
        // "Away from zero" means toward larger absolute values. From 49.5, that's 50 (50 > 49.5 in distance from 0).
        // Wait — 49.5 is positive. Away-from-zero rounds positive midpoints UP (51 from 50.5, 50 from 49.5).
        // For a TRUE -0.5 midpoint we need raw < 0, but our values clamp at [0,100].
        // Construct: prev=1, sum=-1, n=2, K=1 → mean=-0.5, raw=1-0.5=0.5 → 1 (away-from-zero rounds 0.5 → 1).
        var world = World(k: 1m);
        world.Choices.Clear();
        world.Choices["minus_econ"] = new ChoiceDelta { Economy = -1, Environment = 0, Stability = 0 };
        world.Choices["zero"] = new ChoiceDelta { Economy = 0, Environment = 0, Stability = 0 };

        var prev = new WorldStateValue(1, 50, 50, 0);
        var result = WorldStateAggregator.AggregateAndApply(
            prev, new[] { "minus_econ", "zero" }, world);

        // raw = 1 + 1 * (-0.5) = 0.5 → round AwayFromZero = 1
        result.Economy.Should().Be(1);
    }

    [Fact]
    public void Saturation_at_zero()
    {
        // prev=2, all-minus, large K → would underflow, clamp to 0.
        var world = World(k: 100m);
        var prev = new WorldStateValue(2, 50, 50, 0);

        var result = WorldStateAggregator.AggregateAndApply(prev, new[] { "preserve" }, world);
        // sum_economy = -1, mean = -1, raw = 2 + 100*-1 = -98 → clamp to 0
        result.Economy.Should().Be(0);
    }

    [Fact]
    public void Saturation_at_one_hundred()
    {
        var world = World(k: 100m);
        var prev = new WorldStateValue(50, 50, 50, 0);

        var result = WorldStateAggregator.AggregateAndApply(prev, new[] { "build" }, world);
        // sum_economy = 3, mean = 3, raw = 50 + 100*3 = 350 → clamp to 100
        result.Economy.Should().Be(100);
    }

    [Fact]
    public void Unknown_choice_throws()
    {
        var act = () => WorldStateAggregator.AggregateAndApply(
            Neutral, new[] { "build", "explode" }, World());

        act.Should().Throw<UnknownChoiceException>()
            .Which.Code.Should().Be("unknown_choice");
    }

    [Fact]
    public void Choice_lookup_is_case_insensitive()
    {
        // Phase 1.5 #7: WorldOptions.Choices uses OrdinalIgnoreCase comparer.
        // BUILD must resolve to the same delta as build.
        var result = WorldStateAggregator.AggregateAndApply(
            Neutral, new[] { "BUILD", "Build", "build" }, World());

        // 3 builds, mean economy = 3, raw = 50+6 = 56
        result.Economy.Should().Be(56);
        result.Environment.Should().Be(46);
        result.Participants.Should().Be(3);
    }

    [Fact]
    public void K_is_respected()
    {
        var resultK1 = WorldStateAggregator.AggregateAndApply(Neutral, new[] { "build" }, World(k: 1m));
        var resultK4 = WorldStateAggregator.AggregateAndApply(Neutral, new[] { "build" }, World(k: 4m));

        // K=1: raw economy = 50 + 1*3 = 53
        // K=4: raw economy = 50 + 4*3 = 62
        resultK1.Economy.Should().Be(53);
        resultK4.Economy.Should().Be(62);
    }

    [Fact]
    public void Result_does_not_mutate_prev()
    {
        var prev = new WorldStateValue(50, 50, 50, 99);
        WorldStateAggregator.AggregateAndApply(prev, new[] { "build" }, World());
        prev.Should().BeEquivalentTo(new WorldStateValue(50, 50, 50, 99));
    }
}
