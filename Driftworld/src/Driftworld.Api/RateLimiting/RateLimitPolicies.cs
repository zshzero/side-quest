using System.Threading.RateLimiting;
using Driftworld.Api.ErrorHandling;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Driftworld.Api.RateLimiting;

public static class RateLimitPolicies
{
    public const string UserCreatePerIp = "user-create-per-ip";

    public static IServiceCollection AddDriftworldRateLimit(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(UserCreatePerIp, httpContext =>
            {
                var cfg = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitOptions>>().Value.UserCreate;

                var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: key,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = cfg.PermitLimit,
                        Window = TimeSpan.FromSeconds(cfg.WindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });

            options.OnRejected = async (context, ct) =>
            {
                var problemDetails = context.HttpContext.RequestServices
                    .GetRequiredService<IProblemDetailsService>();

                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ts)
                    ? (int)ts.TotalSeconds
                    : -1;

                var extensions = retryAfter > 0
                    ? new Dictionary<string, object?> { ["retry_after_seconds"] = retryAfter }
                    : null;

                await ProblemDetailsWriter.WriteAsync(
                    context.HttpContext,
                    problemDetails,
                    status: StatusCodes.Status429TooManyRequests,
                    code: "rate_limit_exceeded",
                    title: "Rate limit exceeded",
                    detail: "Too many requests from this IP for this endpoint. Try again later.",
                    extensions: extensions,
                    ct: ct);
            };
        });

        return services;
    }
}
