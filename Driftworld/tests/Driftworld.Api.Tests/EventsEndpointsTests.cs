using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Driftworld.Api.Tests;

[Collection(nameof(ApiPostgresCollection))]
public class EventsEndpointsTests
{
    private readonly ApiPostgresFixture _fx;
    public EventsEndpointsTests(ApiPostgresFixture fx) => _fx = fx;

    [Fact]
    public async Task Events_filter_by_cycle_id_returns_events_for_that_cycle()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.OverrideLatestStateAsync(economy: 5, environment: 5, stability: 5);
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        await _fx.RunCycleCloseAsync();
        // Cycle 2 should have all three rule events.

        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/v1/events?cycle_id=2");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(3);
        var types = Enumerable.Range(0, items.GetArrayLength())
            .Select(i => items[i].GetProperty("type").GetString())
            .ToArray();
        types.Should().BeEquivalentTo(new[] { "ecological_collapse", "recession", "unrest" });
    }

    [Fact]
    public async Task Events_with_limit_returns_most_recent_events_descending()
    {
        await _fx.ResetAndSeedAsync();
        await _fx.OverrideLatestStateAsync(economy: 5, environment: 50, stability: 50);
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        await _fx.RunCycleCloseAsync();
        await _fx.BackdateOpenCycleAsync(TimeSpan.FromMinutes(10));
        await _fx.RunCycleCloseAsync();
        // Two recession events: one for cycle 2, one for cycle 3.

        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/v1/events?limit=10");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");

        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("cycleId").GetInt32().Should().Be(3); // most recent first
        items[1].GetProperty("cycleId").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Events_both_filters_returns_400_conflicting_filters()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/v1/events?cycle_id=2&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("conflicting_filters");
    }

    [Fact]
    public async Task Events_limit_above_max_returns_400()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/v1/events?limit=201");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("invalid_limit");
    }

    [Fact]
    public async Task Events_no_filter_uses_default_limit()
    {
        await _fx.ResetAndSeedAsync();
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/v1/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }
}
