using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Driftworld.Data;

public static class ServiceCollectionExtensions
{
    public const string ConnectionStringName = "Driftworld";

    public static IServiceCollection AddDriftworldData(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<DriftworldDbContext>(opts =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionStringName} is not configured. " +
                    $"Set it in appsettings.Development.json (dev) or via env var " +
                    $"ConnectionStrings__{ConnectionStringName} (prod).");

            opts.UseNpgsql(connectionString);
        });
        return services;
    }
}
