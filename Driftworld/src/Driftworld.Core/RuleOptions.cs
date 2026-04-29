namespace Driftworld.Core;

public sealed class RuleOptions
{
    public WorldVariable? Variable { get; init; }
    public ComparisonOp? Op { get; init; }
    public short? Threshold { get; init; }

    public List<RuleOptions>? All { get; init; }

    public bool IsLeaf => All is null;
    public bool IsComposite => All is not null;
}
