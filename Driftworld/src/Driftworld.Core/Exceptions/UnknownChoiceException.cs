namespace Driftworld.Core.Exceptions;

public sealed class UnknownChoiceException : DriftworldException
{
    public UnknownChoiceException(string choice, IReadOnlyCollection<string> validChoices)
        : base(
            code: "unknown_choice",
            httpStatus: 400,
            title: "Unknown choice",
            detail: $"Choice '{choice}' is not in the configured set: {string.Join(", ", validChoices)}.")
    {
        Extensions["choice"] = choice;
        Extensions["valid_choices"] = validChoices;
    }
}
