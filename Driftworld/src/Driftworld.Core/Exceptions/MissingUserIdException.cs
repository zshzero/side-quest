namespace Driftworld.Core.Exceptions;

public sealed class MissingUserIdException : DriftworldException
{
    public MissingUserIdException()
        : base(
            code: "missing_user_id",
            httpStatus: 401,
            title: "X-User-Id header is required",
            detail: "Endpoint requires the X-User-Id header to identify the caller.")
    {
    }
}
