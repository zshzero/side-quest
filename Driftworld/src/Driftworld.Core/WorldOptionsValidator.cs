using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Driftworld.Core;

public sealed partial class WorldOptionsValidator : IValidateOptions<WorldOptions>
{
    private const int MaxRuleDepth = 3;
    private const short MinVariableValue = 0;
    private const short MaxVariableValue = 100;

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public ValidateOptionsResult Validate(string? name, WorldOptions options)
    {
        var errors = new List<string>();

        if (options.K <= 0)
            errors.Add($"{WorldOptions.SectionName}:K must be > 0 (got {options.K}).");

        if (options.K > 100)
            errors.Add($"{WorldOptions.SectionName}:K is suspiciously large ({options.K}); expected single-digit values for stable drift.");

        ValidateChoices(options.Choices, errors);
        ValidateRules(options.Rules, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateChoices(Dictionary<string, ChoiceDelta> choices, List<string> errors)
    {
        if (choices.Count == 0)
        {
            errors.Add($"{WorldOptions.SectionName}:Choices must contain at least one entry.");
            return;
        }

        foreach (var (name, delta) in choices)
        {
            var path = $"{WorldOptions.SectionName}:Choices:{name}";

            if (!IdentifierRegex().IsMatch(name))
                errors.Add($"{path} — name must match [a-z][a-z0-9_]*.");

            if (delta is null)
            {
                errors.Add($"{path} — delta vector is missing.");
                continue;
            }

            // Sanity-bound the deltas. ±100 in one cycle is absurd; flag anything beyond ±25.
            ValidateDelta(path, "Economy", delta.Economy, errors);
            ValidateDelta(path, "Environment", delta.Environment, errors);
            ValidateDelta(path, "Stability", delta.Stability, errors);
        }
    }

    private static void ValidateDelta(string path, string variable, short value, List<string> errors)
    {
        if (value < -25 || value > 25)
            errors.Add($"{path}:{variable} = {value} is out of plausible range [-25, 25].");
    }

    private static void ValidateRules(Dictionary<string, RuleOptions> rules, List<string> errors)
    {
        if (rules.Count == 0)
        {
            errors.Add($"{WorldOptions.SectionName}:Rules must contain at least one entry.");
            return;
        }

        foreach (var (name, rule) in rules)
        {
            var path = $"{WorldOptions.SectionName}:Rules:{name}";

            if (!IdentifierRegex().IsMatch(name))
                errors.Add($"{path} — name must match [a-z][a-z0-9_]*.");

            if (rule is null)
            {
                errors.Add($"{path} — rule body is missing.");
                continue;
            }

            ValidateRule(path, rule, depth: 0, errors);
        }
    }

    private static void ValidateRule(string path, RuleOptions rule, int depth, List<string> errors)
    {
        if (depth >= MaxRuleDepth)
        {
            errors.Add($"{path} — rule nesting depth exceeds {MaxRuleDepth}.");
            return;
        }

        var hasLeaf = rule.Variable is not null || rule.Op is not null || rule.Threshold is not null;
        var hasComposite = rule.All is not null;

        if (hasLeaf && hasComposite)
        {
            errors.Add($"{path} — rule must be either a leaf (Variable/Op/Threshold) or a composite (All), not both.");
            return;
        }

        if (!hasLeaf && !hasComposite)
        {
            errors.Add($"{path} — rule must specify either Variable/Op/Threshold or All.");
            return;
        }

        if (hasLeaf)
        {
            if (rule.Variable is null) errors.Add($"{path}:Variable is required for a leaf rule.");
            if (rule.Op is null) errors.Add($"{path}:Op is required for a leaf rule.");
            if (rule.Threshold is null)
                errors.Add($"{path}:Threshold is required for a leaf rule.");
            else if (rule.Threshold < MinVariableValue || rule.Threshold > MaxVariableValue)
                errors.Add($"{path}:Threshold = {rule.Threshold} must be in [{MinVariableValue}, {MaxVariableValue}].");
        }
        else // composite
        {
            if (rule.All!.Count == 0)
            {
                errors.Add($"{path}:All must contain at least one sub-rule.");
            }
            else
            {
                for (var i = 0; i < rule.All.Count; i++)
                    ValidateRule($"{path}:All[{i}]", rule.All[i], depth + 1, errors);
            }
        }
    }
}
