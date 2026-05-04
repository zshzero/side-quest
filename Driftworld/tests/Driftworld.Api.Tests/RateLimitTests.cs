using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Driftworld.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class RateLimitTests
{
    private readonly ApiPostgresFixture _fx;
    public RateLimitTests(ApiPostgresFixture fx) => _fx = fx;

    /// <summary>
    /// Build a fresh WebApplicationFactory whose rate-limit policy is permits=5,
    /// window=1 second. Lives only for the duration of the test so its limiter state
    /// doesn't leak into other tests in the shared collection.
    /// </summary>
    private WebApplicationFactory<Program> CreateRateLimitedFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Driftworld"] = _fx.ConnectionString,
                    ["Driftworld:World:K"] = "2",
                    ["Driftworld:World:Choices:build:Economy"] = "3",
                    ["Driftworld:World:Choices:build:Environment"] = "-2",
                    ["Driftworld:World:Choices:build:Stability"] = "0",
                    ["Driftworld:World:Rules:recession:Variable"] = "Economy",
                    ["Driftworld:World:Rules:recession:Op"] = "Lt",
                    ["Driftworld:World:Rules:recession:Threshold"] = "20",
                    ["Driftworld:RateLimit:UserCreate:PermitLimit"] = "5",
                    ["Driftworld:RateLimit:UserCreate:WindowSeconds"] = "60",
                });
            });
            builder.ConfigureServices(services => services.AddSingleton(_fx.Clock));
        });
    }

    [Fact]
    public async Task Sixth_user_create_in_same_window_returns_429_problemdetails()
    {
        await _fx.ResetAndSeedAsync();

        await using var factory = CreateRateLimitedFactory();
        // Make sure schema is current via the same connection.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DriftworldDbContext>();
            await db.Database.MigrateAsync();
        }

        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.PostAsJsonAsync("/v1/users", new { handle = $"rl_user_{i}" });
            statuses.Add(resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                rejected = resp;
        }

        statuses.Take(5).Should().AllBeEquivalentTo(HttpStatusCode.Created);
        statuses[5].Should().Be(HttpStatusCode.TooManyRequests);

        rejected.Should().NotBeNull();
        rejected!.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(429);
        problem.GetProperty("code").GetString().Should().Be("rate_limit_exceeded");
    }
}
