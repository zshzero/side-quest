namespace Driftworld.Core.Exceptions;

public sealed class InvalidLimitException : DriftworldException
{
    public InvalidLimitException(int received, int max, string reason)
        : base(
            code: "invalid_limit",
            httpStatus: 400,
            title: "limit query parameter is out of range",
            detail: $"limit={received} {reason} (max={max}).")
    {
        Extensions["received"] = received;
        Extensions["max"] = max;
    }
}
