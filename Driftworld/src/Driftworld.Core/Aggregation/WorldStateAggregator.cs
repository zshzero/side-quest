using Driftworld.Core.Exceptions;

namespace Driftworld.Core.Aggregation;

public static class WorldStateAggregator
{
    public static WorldStateValue AggregateAndApply(
        WorldStateValue prev,
        IReadOnlyList<string> choices,
        WorldOptions world)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(world);

        if (choices.Count == 0)
            return prev with { Participants = 0 };

        int sumEconomy = 0, sumEnvironment = 0, sumStability = 0;

        foreach (var name in choices)
        {
            if (!world.Choices.TryGetValue(name, out var delta))
                throw new UnknownChoiceException(name, world.Choices.Keys.ToArray());

            sumEconomy += delta.Economy;
            sumEnvironment += delta.Environment;
            sumStability += delta.Stability;
        }

        var n = (decimal)choices.Count;
        var k = world.K;

        return new WorldStateValue(
            Economy: ApplyOne(prev.Economy, sumEconomy, n, k),
            Environment: ApplyOne(prev.Environment, sumEnvironment, n, k),
            Stability: ApplyOne(prev.Stability, sumStability, n, k),
            Participants: choices.Count);
    }

    private static short ApplyOne(short prev, int sum, decimal n, decimal k)
    {
        var meanDelta = (decimal)sum / n;
        var raw = (decimal)prev + k * meanDelta;
        var rounded = Math.Round(raw, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp((int)rounded, 0, 100);
    }
}
