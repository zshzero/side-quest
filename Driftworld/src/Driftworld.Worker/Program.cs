using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Cycles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

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
            "Closed {Count} cycle(s): {Ids}.",
            result.CyclesClosed,
            string.Join(", ", result.ClosedCycleIds));
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

public partial class Program;
