namespace Driftworld.Core.Exceptions;

public sealed class InvalidHandleException : DriftworldException
{
    public InvalidHandleException(string handle, IReadOnlyCollection<string> reasons)
        : base(
            code: "invalid_handle",
            httpStatus: 400,
            title: "Handle is not valid",
            detail: $"Handle '{handle}' does not satisfy: {string.Join("; ", reasons)}.")
    {
        Extensions["handle"] = handle;
        Extensions["reasons"] = reasons;
    }
}
