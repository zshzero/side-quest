using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class DecisionsEndpointsTests
{
    private readonly ApiPostgresFixture _fx;
    public DecisionsEndpointsTests(ApiPostgresFixture fx) => _fx = fx;

    private async Task<Guid> CreateUserAsync(HttpClient client, string? handle = null)
    {
        var response = await client.PostAsJsonAsync("/v1/users", new { handle });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("userId").GetGuid();
    }

    private static HttpRequestMessage Post(string url, object body, Guid? userId = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        if (userId is not null) req.Headers.Add("X-User-Id", userId.Value.ToString());
        return req;
    }

    [Fact]
    public async Task Happy_path_returns_201_and_records_decision()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client, "ada");

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }, userId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("decisionId").GetGuid().Should().NotBeEmpty();
        body.GetProperty("cycleId").GetInt32().Should().Be(2); // genesis open cycle
    }

    [Fact]
    public async Task Choice_lookup_is_case_insensitive()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client);

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "BUILD" }, userId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Duplicate_decision_in_same_cycle_returns_409()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client);

        var first = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }, userId));
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.SendAsync(Post("/v1/decisions", new { choice = "preserve" }, userId));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("code").GetString().Should().Be("duplicate");
        problem.GetProperty("cycle_id").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Missing_user_id_returns_401()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("missing_user_id");
    }

    [Fact]
    public async Task Malformed_user_id_returns_400()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/decisions")
        {
            Content = JsonContent.Create(new { choice = "build" }),
        };
        req.Headers.Add("X-User-Id", "not-a-uuid");

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("malformed_user_id");
    }

    [Fact]
    public async Task Unknown_user_returns_401()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var nonexistent = Guid.NewGuid();

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }, nonexistent));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("unknown_user");
    }

    [Fact]
    public async Task Unknown_choice_returns_400()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client);

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "explode" }, userId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("unknown_choice");
    }

    [Fact]
    public async Task No_open_cycle_returns_503()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.CloseOpenCycleAsync();

        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client);

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }, userId));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("no_open_cycle");
    }

    [Fact]
    public async Task Decision_bumps_user_last_seen_at()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        // Create user with a clock-controlled CreatedAt; then submit a decision.
        var userId = await CreateUserAsync(client, "ada");

        var response = await client.SendAsync(Post("/v1/decisions", new { choice = "build" }, userId));
        response.EnsureSuccessStatusCode();

        // last_seen_at == clock.UtcNow because both creation and decision happen at the same fixed time.
        // Real check: just confirm it's been written (non-null and equals now).
        // Reading via DbContext requires a scope from the factory.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Driftworld.Data.DriftworldDbContext>();
        var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.Users, u => u.Id == userId);
        user.LastSeenAt.Should().NotBeNull();
        user.LastSeenAt.Should().Be(_fx.Clock.GetUtcNow().UtcDateTime);
    }
}
