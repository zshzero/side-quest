using Driftworld.Core;
using FluentAssertions;
using Xunit;

namespace Driftworld.Core.Tests;

public class WorldOptionsValidatorTests
{
    private readonly WorldOptionsValidator _sut = new();

    private static WorldOptions ValidOptions() => new()
    {
        K = 2,
        Choices =
        {
            ["build"] = new ChoiceDelta { Economy = 3, Environment = -2, Stability = 0 },
        },
        Rules =
        {
            ["recession"] = new RuleOptions { Variable = WorldVariable.Economy, Op = ComparisonOp.Lt, Threshold = 20 },
        },
    };

    [Fact]
    public void Valid_options_pass()
    {
        _sut.Validate(null, ValidOptions()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void K_must_be_positive(decimal k)
    {
        var opts = ValidOptions();
        opts.K = k;
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*K must be > 0*");
    }

    [Fact]
    public void K_above_100_is_flagged_as_suspicious()
    {
        var opts = ValidOptions();
        opts.K = 200;
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*suspiciously large*");
    }

    [Fact]
    public void Empty_choices_fails()
    {
        var opts = ValidOptions();
        opts.Choices.Clear();
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*Choices must contain at least one entry*");
    }

    [Fact]
    public void Empty_rules_fails()
    {
        var opts = ValidOptions();
        opts.Rules.Clear();
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*Rules must contain at least one entry*");
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("123start")]
    [InlineData("with-dashes")]
    public void Choice_name_must_match_identifier_regex(string name)
    {
        var opts = ValidOptions();
        opts.Choices.Clear();
        opts.Choices[name] = new ChoiceDelta { Economy = 1, Environment = 0, Stability = 0 };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*[a-z]*");
    }

    [Theory]
    [InlineData((short)26)]
    [InlineData((short)-26)]
    public void Choice_delta_outside_plausible_range_fails(short value)
    {
        var opts = ValidOptions();
        opts.Choices["build"] = new ChoiceDelta { Economy = value, Environment = 0, Stability = 0 };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch($"*Economy = {value}*");
    }

    [Fact]
    public void Rule_with_both_leaf_and_composite_fields_fails()
    {
        var opts = ValidOptions();
        opts.Rules["bad"] = new RuleOptions
        {
            Variable = WorldVariable.Economy,
            Op = ComparisonOp.Lt,
            Threshold = 20,
            All = new() { new RuleOptions { Variable = WorldVariable.Stability, Op = ComparisonOp.Lt, Threshold = 20 } },
        };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*either a leaf*or a composite*");
    }

    [Fact]
    public void Rule_with_neither_leaf_nor_composite_fails()
    {
        var opts = ValidOptions();
        opts.Rules["empty"] = new RuleOptions();
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*must specify either Variable/Op/Threshold or All*");
    }

    [Fact]
    public void Leaf_rule_with_threshold_out_of_range_fails()
    {
        var opts = ValidOptions();
        opts.Rules["bad"] = new RuleOptions { Variable = WorldVariable.Economy, Op = ComparisonOp.Lt, Threshold = 101 };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*Threshold = 101*");
    }

    [Fact]
    public void Composite_rule_with_empty_all_fails()
    {
        var opts = ValidOptions();
        opts.Rules["bad"] = new RuleOptions { All = new() };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*All must contain at least one sub-rule*");
    }

    [Fact]
    public void Deeply_nested_rule_fails()
    {
        var deepest = new RuleOptions { Variable = WorldVariable.Economy, Op = ComparisonOp.Lt, Threshold = 20 };
        var d3 = new RuleOptions { All = new() { deepest } };
        var d2 = new RuleOptions { All = new() { d3 } };
        var d1 = new RuleOptions { All = new() { d2 } };

        var opts = ValidOptions();
        opts.Rules["nested"] = d1;
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainMatch("*nesting depth exceeds*");
    }

    [Fact]
    public void Multiple_problems_are_reported_together()
    {
        var opts = new WorldOptions
        {
            K = -1,
            // empty Choices
            // empty Rules
        };
        var result = _sut.Validate(null, opts);
        result.Failed.Should().BeTrue();
        result.Failures.Should().HaveCountGreaterThanOrEqualTo(3);
    }
}
