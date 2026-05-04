namespace Driftworld.Api.RateLimiting;

public sealed class RateLimitOptions
{
    public const string SectionName = "Driftworld:RateLimit";

    public PolicyOptions UserCreate { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };

    public sealed class PolicyOptions
    {
        public int PermitLimit { get; set; }
        public int WindowSeconds { get; set; }
    }
}
