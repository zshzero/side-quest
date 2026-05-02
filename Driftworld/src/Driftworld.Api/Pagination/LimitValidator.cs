using Driftworld.Core.Exceptions;

namespace Driftworld.Api.Pagination;

public static class LimitValidator
{
    public static int Validate(int? limit, int defaultValue, int max)
    {
        if (limit is null) return defaultValue;
        if (limit.Value < 1)
            throw new InvalidLimitException(limit.Value, max, "must be ≥ 1");
        if (limit.Value > max)
            throw new InvalidLimitException(limit.Value, max, $"must be ≤ {max}");
        return limit.Value;
    }
}
