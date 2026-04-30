using Driftworld.Api;
using Driftworld.Api.Endpoints;
using Driftworld.Core;
using Driftworld.Data;
using Driftworld.Data.Seeding;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDriftworldData(builder.Configuration);
builder.Services.AddDriftworldApi();
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

app.UseExceptionHandler();

app.MapGet("/", () => Results.Ok(new
{
    service = "Driftworld",
    phase = 2,
    status = "users + decisions endpoints",
}));

var v1 = app.MapGroup("/v1");
v1.MapUsersEndpoints();
v1.MapDecisionsEndpoints();

app.Run();

public partial class Program;
