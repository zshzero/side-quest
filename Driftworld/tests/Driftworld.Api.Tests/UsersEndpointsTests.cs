using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class UsersEndpointsTests
{
    private readonly ApiPostgresFixture _fx;
    public UsersEndpointsTests(ApiPostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_user_with_handle_returns_201()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/users", new { handle = "ada" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("userId").GetGuid().Should().NotBeEmpty();
        body.GetProperty("handle").GetString().Should().Be("ada");
    }

    [Fact]
    public async Task Create_user_anonymous_returns_201_with_null_handle()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/users", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("userId").GetGuid().Should().NotBeEmpty();
        body.GetProperty("handle").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Duplicate_handle_returns_409_problemdetails()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var first = await client.PostAsJsonAsync("/v1/users", new { handle = "duplicate" });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/v1/users", new { handle = "duplicate" });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("code").GetString().Should().Be("duplicate_handle");
        problem.GetProperty("handle").GetString().Should().Be("duplicate");
    }

    [Theory]
    [InlineData("ab")]            // too short
    [InlineData("with space")]    // illegal char
    [InlineData("a!")]            // illegal char
    public async Task Invalid_handle_returns_400_problemdetails(string handle)
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/users", new { handle });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(400);
        problem.GetProperty("code").GetString().Should().Be("invalid_handle");
    }
}
