using Driftworld.Api;
using Driftworld.Api.Endpoints;
using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Seeding;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine("logs", "driftworld-api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7));

builder.Services.AddDriftworldData(builder.Configuration);
builder.Services.AddDriftworldApi(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services
    .AddDriftworldOptions(builder.Configuration)
    .ValidateOnStart();

var app = builder.Build();

if (args.Contains("--seed"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

    await db.Database.MigrateAsync();
    var result = await GenesisSeeder.EnsureSeededAsync(db, clock);

    Console.WriteLine(result.Applied
        ? $"Seed applied. T0 = {result.T0:O}, open cycle id = {result.OpenCycleId}."
        : $"Seed already present. Open cycle id = {result.OpenCycleId} (starts {result.T0:O}). No-op.");
    return;
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseRateLimiter();

app.MapGet("/", () => Results.Ok(new
{
    service = "Driftworld",
    phase = 5,
    status = "ready",
}));

var v1 = app.MapGroup("/v1");
v1.MapUsersEndpoints();
v1.MapDecisionsEndpoints();
v1.MapWorldEndpoints();
v1.MapEventsEndpoints();
v1.MapContributionEndpoints();

app.Run();

public partial class Program;
