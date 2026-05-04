using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Cycles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;

var bootstrapLogger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();
Log.Logger = bootstrapLogger;

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(new LoggerConfiguration()
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            path: Path.Combine("logs", "driftworld-worker-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger(), dispose: true);

    builder.Services.AddDriftworldData(builder.Configuration);
    builder.Services
        .AddDriftworldOptions(builder.Configuration)
        .ValidateOnStart();
    builder.Services.AddSingleton(TimeProvider.System);

    using var host = builder.Build();
    await host.StartAsync();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();

    try
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
        var world = scope.ServiceProvider.GetRequiredService<IOptions<WorldOptions>>().Value;
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var result = await CycleCloser.RunAsync(db, world, clock);

        if (result.CyclesClosed == 0)
        {
            logger.LogInformation("No cycles due. World unchanged.");
        }
        else
        {
            logger.LogInformation(
                "Closed {Count} cycle(s): {ClosedCycleIds}.",
                result.CyclesClosed, result.ClosedCycleIds);
        }

        await host.StopAsync();
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Cycle close run failed.");
        await host.StopAsync();
        return 1;
    }
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
