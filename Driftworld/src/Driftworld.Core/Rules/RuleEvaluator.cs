using Driftworld.Core.Aggregation;

namespace Driftworld.Core.Rules;

public static class RuleEvaluator
{
    /// <summary>
    /// Returns the names of every rule whose body evaluates to true against
    /// <paramref name="state"/>. Order is alphabetical by rule name (stable for
    /// clients).
    /// </summary>
    public static IReadOnlyList<string> EvaluateMatching(WorldStateValue state, WorldOptions options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        var matches = new List<string>();
        foreach (var (name, rule) in options.Rules)
        {
            if (IsRuleHolding(state, rule))
                matches.Add(name);
        }
        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    /// <summary>
    /// Recursive predicate. Validator (<see cref="WorldOptionsValidator"/>) guarantees
    /// well-formed leaf or composite shapes; we trust those invariants here.
    /// </summary>
    public static bool IsRuleHolding(WorldStateValue state, RuleOptions rule)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsComposite)
            return rule.All!.All(sub => IsRuleHolding(state, sub));

        var value = state.GetVariable(rule.Variable!.Value);
        var threshold = rule.Threshold!.Value;
        return rule.Op!.Value switch
        {
            ComparisonOp.Lt => value < threshold,
            ComparisonOp.Lte => value <= threshold,
            ComparisonOp.Gt => value > threshold,
            ComparisonOp.Gte => value >= threshold,
            ComparisonOp.Eq => value == threshold,
            _ => throw new InvalidOperationException($"Unknown comparison op: {rule.Op}"),
        };
    }
}
