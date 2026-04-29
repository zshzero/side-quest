using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Driftworld.Core;

public static class ServiceCollectionExtensions
{
    public static OptionsBuilder<WorldOptions> AddDriftworldOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<WorldOptions>, WorldOptionsValidator>();

        return services
            .AddOptions<WorldOptions>()
            .Bind(configuration.GetSection(WorldOptions.SectionName));
    }
}
