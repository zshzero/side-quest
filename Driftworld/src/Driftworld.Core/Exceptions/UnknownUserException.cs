namespace Driftworld.Core.Exceptions;

public sealed class UnknownUserException : DriftworldException
{
    public UnknownUserException(Guid userId)
        : base(
            code: "unknown_user",
            httpStatus: 401,
            title: "Unknown user",
            detail: $"No user with id {userId} exists.")
    {
        Extensions["user_id"] = userId;
    }
}
