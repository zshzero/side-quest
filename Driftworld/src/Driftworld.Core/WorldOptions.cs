namespace Driftworld.Core;

public sealed class WorldOptions
{
    public const string SectionName = "Driftworld:World";

    public decimal K { get; set; }

    public Dictionary<string, ChoiceDelta> Choices { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, RuleOptions> Rules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
