using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using Xunit;

namespace Driftworld.Api.Tests;

public class HostStartupTests
{
    [Fact]
    public async Task Host_starts_with_valid_config_and_responds_to_root()
    {
        await using var factory = new DriftworldFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Host_fails_to_start_when_world_K_is_missing()
    {
        var factory = new DriftworldFactory(removeK: true);

        // CreateClient triggers WebHost startup — ValidateOnStart should throw.
        var act = () => factory.CreateClient();
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*K must be > 0*");

        factory.Dispose();
    }

    private sealed class DriftworldFactory : WebApplicationFactory<Program>
    {
        private readonly bool _removeK;

        public DriftworldFactory(bool removeK = false) => _removeK = removeK;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var dict = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Driftworld"] = "Host=unreachable;Port=1;Database=x;Username=x;Password=x",
                    ["Driftworld:World:Choices:build:Economy"] = "3",
                    ["Driftworld:World:Choices:build:Environment"] = "-2",
                    ["Driftworld:World:Choices:build:Stability"] = "0",
                    ["Driftworld:World:Rules:recession:Variable"] = "Economy",
                    ["Driftworld:World:Rules:recession:Op"] = "Lt",
                    ["Driftworld:World:Rules:recession:Threshold"] = "20",
                };
                if (!_removeK)
                    dict["Driftworld:World:K"] = "2";
                cfg.AddInMemoryCollection(dict);
            });
        }
    }
}
