using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class ContributionEndpointsTests
{
    private readonly ApiPostgresFixture _fx;
    public ContributionEndpointsTests(ApiPostgresFixture fx) => _fx = fx;

    private async Task<Guid> CreateUserAsync(HttpClient client, string handle)
    {
        var response = await client.PostAsJsonAsync("/v1/users", new { handle });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid();
    }

    private static HttpRequestMessage Decision(Guid userId, string choice)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/decisions")
        {
            Content = JsonContent.Create(new { choice }),
        };
        req.Headers.Add("X-User-Id", userId.ToString());
        return req;
    }

    [Fact]
    public async Task Empty_user_returns_zero_total_and_zero_alignment()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client, "ada");

        var response = await client.GetAsync($"/v1/users/{userId}/contribution");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalDecisions").GetInt32().Should().Be(0);
        body.GetProperty("byChoice").EnumerateObject().Should().BeEmpty();
        body.GetProperty("alignment").GetProperty("withMajorityPct").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Unknown_user_returns_401()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync($"/v1/users/{Guid.NewGuid()}/contribution");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("unknown_user");
    }

    [Fact]
    public async Task Single_decision_user_aligned_with_self()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var userId = await CreateUserAsync(client, "ada");
        await client.SendAsync(Decision(userId, "build"));

        var response = await client.GetAsync($"/v1/users/{userId}/contribution");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalDecisions").GetInt32().Should().Be(1);
        body.GetProperty("byChoice").GetProperty("build").GetInt32().Should().Be(1);
        // Sole decider — they ARE the majority.
        body.GetProperty("alignment").GetProperty("withMajorityPct").GetInt32().Should().Be(100);
    }

    [Fact]
    public async Task Alignment_math_handles_minority_case()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();
        var alice = await CreateUserAsync(client, "alice");
        var bob = await CreateUserAsync(client, "bob");
        var carol = await CreateUserAsync(client, "carol");

        await client.SendAsync(Decision(alice, "build"));
        await client.SendAsync(Decision(bob, "build"));
        await client.SendAsync(Decision(carol, "preserve"));
        // Modal in cycle 2 = "build". Carol is the minority.

        var response = await client.GetAsync($"/v1/users/{carol}/contribution");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalDecisions").GetInt32().Should().Be(1);
        body.GetProperty("byChoice").GetProperty("preserve").GetInt32().Should().Be(1);
        body.GetProperty("alignment").GetProperty("withMajorityPct").GetInt32().Should().Be(0);

        var aliceResp = await client.GetAsync($"/v1/users/{alice}/contribution");
        var aliceBody = await aliceResp.Content.ReadFromJsonAsync<JsonElement>();
        aliceBody.GetProperty("alignment").GetProperty("withMajorityPct").GetInt32().Should().Be(100);
    }
}
