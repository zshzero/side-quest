namespace Driftworld.Core.Exceptions;

public sealed class ConflictingFiltersException : DriftworldException
{
    public ConflictingFiltersException(params string[] filterNames)
        : base(
            code: "conflicting_filters",
            httpStatus: 400,
            title: "Mutually exclusive query parameters supplied",
            detail: $"Supply at most one of: {string.Join(", ", filterNames)}.")
    {
        Extensions["filters"] = filterNames;
    }
}
