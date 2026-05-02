using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class WorldEndpointsTests
{
    private readonly ApiPostgresFixture _fx;
    public WorldEndpointsTests(ApiPostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Current_returns_genesis_state_with_no_active_events()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/v1/world/current");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("cycle").GetProperty("id").GetInt32().Should().Be(2);
        body.GetProperty("cycle").GetProperty("status").GetString().Should().Be("open");
        body.GetProperty("state").GetProperty("economy").GetInt32().Should().Be(50);
        body.GetProperty("state").GetProperty("asOfCycleId").GetInt32().Should().Be(1);
        body.GetProperty("activeEvents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Current_reports_recession_active_with_correct_since_cycle_id()
    {
        await _fx.ResetAndSeedAsync();
        // Make genesis state already in recession.
        await _fx.OverrideLatestStateAsync(economy: 15, environment: 50, stability: 50);

        // Close cycle 2: state copies forward (no decisions), recession event written.
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        await _fx.RunCycleCloseAsync();

        // Close cycle 3: same — state stays in recession.
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        await _fx.RunCycleCloseAsync();

        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/v1/world/current");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var active = body.GetProperty("activeEvents");
        active.GetArrayLength().Should().Be(1);
        active[0].GetProperty("type").GetString().Should().Be("recession");
        // Walking backward from cycle 3 → 3, 2, 1 all in recession → since = 1.
        active[0].GetProperty("sinceCycleId").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Current_drops_recovered_event_without_writing_a_db_row()
    {
        await _fx.ResetAndSeedAsync();
        // Cycle 1 (genesis): healthy.
        await _fx.OverrideLatestStateAsync(economy: 21, environment: 50, stability: 50);

        // Cycle 2: dive into recession via a preserve choice.
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        var client = _fx.Factory.CreateClient();
        var userResp = await client.PostAsJsonAsync("/v1/users", new { handle = "alice" });
        var userId = (await userResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid();
        var dec = new HttpRequestMessage(HttpMethod.Post, "/v1/decisions")
        {
            Content = JsonContent.Create(new { choice = "preserve" }),
        };
        dec.Headers.Add("X-User-Id", userId.ToString());
        await client.SendAsync(dec);
        await _fx.RunCycleCloseAsync();
        // After cycle 2: economy = 21 + 2*-1 = 19 → recession holds; event written.

        var afterRecess = await client.GetAsync("/v1/world/current");
        var bodyR = await afterRecess.Content.ReadFromJsonAsync<JsonElement>();
        bodyR.GetProperty("activeEvents").GetArrayLength().Should().Be(1);

        // Cycle 3: post a build decision to push economy back above 20.
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        var userResp2 = await client.PostAsJsonAsync("/v1/users", new { handle = "bob" });
        var userId2 = (await userResp2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetGuid();
        var dec2 = new HttpRequestMessage(HttpMethod.Post, "/v1/decisions")
        {
            Content = JsonContent.Create(new { choice = "build" }),
        };
        dec2.Headers.Add("X-User-Id", userId2.ToString());
        await client.SendAsync(dec2);
        await _fx.RunCycleCloseAsync();
        // After cycle 3: economy = 19 + 2*3 = 25 → recession DOES NOT hold.

        // Active events should be empty — read-time evaluation against latest state.
        var afterRecover = await client.GetAsync("/v1/world/current");
        var bodyA = await afterRecover.Content.ReadFromJsonAsync<JsonElement>();
        bodyA.GetProperty("activeEvents").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task History_default_limit_returns_30_or_fewer_items_descending_by_cycle_id()
    {
        await _fx.ResetAndSeedAsync();
        // Add a couple of closed cycles via worker.
        for (var i = 0; i < 3; i++)
        {
            await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
            await _fx.RunCycleCloseAsync();
        }

        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/v1/world/history");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");

        items.GetArrayLength().Should().Be(4); // genesis + 3 newly-closed
        var ids = Enumerable.Range(0, items.GetArrayLength()).Select(i => items[i].GetProperty("cycleId").GetInt32()).ToArray();
        ids.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task History_respects_explicit_limit()
    {
        await _fx.ResetAndSeedAsync();
        for (var i = 0; i < 3; i++)
        {
            await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
            await _fx.RunCycleCloseAsync();
        }

        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/v1/world/history?limit=2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task History_out_of_range_limit_returns_400_problemdetails(int limit)
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync($"/v1/world/history?limit={limit}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("invalid_limit");
        problem.GetProperty("received").GetInt32().Should().Be(limit);
    }
}
