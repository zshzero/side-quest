using Driftworld.Core;
using FluentAssertions;
using Xunit;

namespace Driftworld.Core.Tests;

public class ChoiceDeltaTests
{
    private readonly ChoiceDelta _delta = new() { Economy = 3, Environment = -2, Stability = 1 };

    [Theory]
    [InlineData(WorldVariable.Economy, (short)3)]
    [InlineData(WorldVariable.Environment, (short)-2)]
    [InlineData(WorldVariable.Stability, (short)1)]
    public void For_returns_correct_value_per_variable(WorldVariable v, short expected)
    {
        _delta.For(v).Should().Be(expected);
    }

    [Fact]
    public void For_throws_for_unknown_enum_value()
    {
        var act = () => _delta.For((WorldVariable)999);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void All_enum_values_are_handled()
    {
        // If a future PR adds a new WorldVariable, this test catches the
        // un-updated switch in ChoiceDelta.For without needing a separate audit.
        foreach (var v in Enum.GetValues<WorldVariable>())
        {
            var act = () => _delta.For(v);
            act.Should().NotThrow();
        }
    }
}
