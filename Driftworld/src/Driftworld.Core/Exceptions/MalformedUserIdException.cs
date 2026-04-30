namespace Driftworld.Core.Exceptions;

public sealed class MalformedUserIdException : DriftworldException
{
    public MalformedUserIdException(string raw)
        : base(
            code: "malformed_user_id",
            httpStatus: 400,
            title: "X-User-Id header is malformed",
            detail: "X-User-Id must be a UUID.")
    {
        Extensions["received"] = raw;
    }
}
