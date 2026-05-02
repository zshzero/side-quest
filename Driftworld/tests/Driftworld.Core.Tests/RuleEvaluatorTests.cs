using Driftworld.Core;
using Driftworld.Core.Aggregation;
using Driftworld.Core.Rules;
using FluentAssertions;
using Xunit;

namespace Driftworld.Core.Tests;

public class RuleEvaluatorTests
{
    private static WorldOptions WithRules(params (string name, RuleOptions rule)[] rules)
    {
        var opts = new WorldOptions
        {
            K = 2,
            Choices = { ["build"] = new ChoiceDelta { Economy = 3, Environment = -2, Stability = 0 } },
        };
        foreach (var (name, rule) in rules) opts.Rules[name] = rule;
        return opts;
    }

    private static RuleOptions Leaf(WorldVariable v, ComparisonOp op, short threshold) =>
        new() { Variable = v, Op = op, Threshold = threshold };

    private static RuleOptions Composite(params RuleOptions[] subs) =>
        new() { All = subs.ToList() };

    [Theory]
    [InlineData(ComparisonOp.Lt, (short)19, true)]
    [InlineData(ComparisonOp.Lt, (short)20, false)]
    [InlineData(ComparisonOp.Lte, (short)20, true)]
    [InlineData(ComparisonOp.Lte, (short)21, false)]
    [InlineData(ComparisonOp.Gt, (short)21, true)]
    [InlineData(ComparisonOp.Gt, (short)20, false)]
    [InlineData(ComparisonOp.Gte, (short)20, true)]
    [InlineData(ComparisonOp.Gte, (short)19, false)]
    [InlineData(ComparisonOp.Eq, (short)20, true)]
    [InlineData(ComparisonOp.Eq, (short)21, false)]
    public void Leaf_rule_each_op_at_threshold(ComparisonOp op, short economy, bool expected)
    {
        var state = new WorldStateValue(economy, 50, 50, 0);
        var rule = Leaf(WorldVariable.Economy, op, 20);
        RuleEvaluator.IsRuleHolding(state, rule).Should().Be(expected);
    }

    [Fact]
    public void Composite_all_holds_only_when_every_subrule_holds()
    {
        var rule = Composite(
            Leaf(WorldVariable.Economy, ComparisonOp.Gte, 70),
            Leaf(WorldVariable.Environment, ComparisonOp.Gte, 70),
            Leaf(WorldVariable.Stability, ComparisonOp.Gte, 70));

        RuleEvaluator.IsRuleHolding(new WorldStateValue(70, 70, 70, 0), rule).Should().BeTrue();
        RuleEvaluator.IsRuleHolding(new WorldStateValue(70, 70, 69, 0), rule).Should().BeFalse();
        RuleEvaluator.IsRuleHolding(new WorldStateValue(100, 70, 70, 0), rule).Should().BeTrue();
    }

    [Fact]
    public void Composite_at_max_depth_3_evaluates()
    {
        // depth 3: outer composite of composite of composite of leaf
        var depth3 = Composite(
            Composite(
                Composite(Leaf(WorldVariable.Economy, ComparisonOp.Lt, 20))));
        RuleEvaluator.IsRuleHolding(new WorldStateValue(15, 50, 50, 0), depth3).Should().BeTrue();
        RuleEvaluator.IsRuleHolding(new WorldStateValue(25, 50, 50, 0), depth3).Should().BeFalse();
    }

    [Fact]
    public void EvaluateMatching_returns_alphabetically_sorted_rule_names()
    {
        var opts = WithRules(
            ("zebra_rule", Leaf(WorldVariable.Economy, ComparisonOp.Lt, 100)),
            ("apple_rule", Leaf(WorldVariable.Economy, ComparisonOp.Lt, 100)),
            ("middle_rule", Leaf(WorldVariable.Economy, ComparisonOp.Lt, 100)));

        var matches = RuleEvaluator.EvaluateMatching(new WorldStateValue(50, 50, 50, 0), opts);

        matches.Should().Equal("apple_rule", "middle_rule", "zebra_rule");
    }

    [Fact]
    public void EvaluateMatching_with_no_rules_returns_empty()
    {
        var opts = WithRules();
        var matches = RuleEvaluator.EvaluateMatching(new WorldStateValue(15, 15, 15, 0), opts);
        matches.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateMatching_with_no_holding_rules_returns_empty()
    {
        var opts = WithRules(
            ("recession", Leaf(WorldVariable.Economy, ComparisonOp.Lt, 20)),
            ("ecological_collapse", Leaf(WorldVariable.Environment, ComparisonOp.Lt, 15)));

        var matches = RuleEvaluator.EvaluateMatching(new WorldStateValue(80, 80, 80, 0), opts);
        matches.Should().BeEmpty();
    }

    [Fact]
    public void EvaluateMatching_filters_to_only_holding_rules()
    {
        var opts = WithRules(
            ("recession", Leaf(WorldVariable.Economy, ComparisonOp.Lt, 20)),
            ("golden_age", Composite(
                Leaf(WorldVariable.Economy, ComparisonOp.Gte, 70),
                Leaf(WorldVariable.Environment, ComparisonOp.Gte, 70),
                Leaf(WorldVariable.Stability, ComparisonOp.Gte, 70))));

        var matches = RuleEvaluator.EvaluateMatching(new WorldStateValue(15, 50, 50, 0), opts);
        matches.Should().ContainSingle().Which.Should().Be("recession");
    }

    [Theory]
    [InlineData(WorldVariable.Economy, (short)10)]
    [InlineData(WorldVariable.Environment, (short)10)]
    [InlineData(WorldVariable.Stability, (short)10)]
    public void Each_world_variable_can_be_referenced(WorldVariable v, short value)
    {
        var rule = Leaf(v, ComparisonOp.Lt, 20);
        var state = v switch
        {
            WorldVariable.Economy => new WorldStateValue(value, 50, 50, 0),
            WorldVariable.Environment => new WorldStateValue(50, value, 50, 0),
            WorldVariable.Stability => new WorldStateValue(50, 50, value, 0),
            _ => throw new ArgumentOutOfRangeException(),
        };
        RuleEvaluator.IsRuleHolding(state, rule).Should().BeTrue();
    }
}
