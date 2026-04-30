namespace Driftworld.Core.Exceptions;

public sealed class DuplicateDecisionException : DriftworldException
{
    public DuplicateDecisionException(Guid userId, int cycleId)
        : base(
            code: "duplicate",
            httpStatus: 409,
            title: "Decision already submitted for this cycle",
            detail: $"User {userId} has already submitted a decision for cycle {cycleId}.")
    {
        Extensions["user_id"] = userId;
        Extensions["cycle_id"] = cycleId;
    }
}
