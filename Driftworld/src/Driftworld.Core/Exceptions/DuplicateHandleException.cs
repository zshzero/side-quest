namespace Driftworld.Core.Exceptions;

public sealed class DuplicateHandleException : DriftworldException
{
    public DuplicateHandleException(string handle)
        : base(
            code: "duplicate_handle",
            httpStatus: 409,
            title: "Handle already taken",
            detail: $"Handle '{handle}' is already registered to another user.")
    {
        Extensions["handle"] = handle;
    }
}
