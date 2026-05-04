using Driftworld.Api.ErrorHandling;
using Driftworld.Api.RateLimiting;
using Driftworld.Api.Validation;
using FluentValidation;

namespace Driftworld.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDriftworldApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<DriftworldExceptionHandler>();

        services.AddSingleton<IValidator<string>, HandleValidator>();

        services.AddDriftworldRateLimit(configuration);

        return services;
    }
}
