using HagiCode.Libs.Skills;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddHagiCodeSkills_registers_default_online_api_services()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeSkills();

        await using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();
        var provider = serviceProvider.GetRequiredService<IOnlineApiEndpointProvider>();

        client.ShouldBeOfType<OnlineApiClient>();
        provider.ShouldBeOfType<VercelOnlineApiEndpointProvider>();
    }

    [Fact]
    public async Task AddHagiCodeSkills_allows_endpoint_provider_override()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeSkills();
        services.AddSingleton<IOnlineApiEndpointProvider, CustomEndpointProvider>();

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IOnlineApiEndpointProvider>();

        provider.ShouldBeOfType<CustomEndpointProvider>();
    }

    private sealed class CustomEndpointProvider : IOnlineApiEndpointProvider
    {
        public OnlineApiEndpointProfile GetSearchEndpoint() => new(OnlineApiOperation.Search, new Uri("https://custom.example/"), "search");

        public OnlineApiEndpointProfile GetAuditEndpoint() => new(OnlineApiOperation.Audit, new Uri("https://custom.example/"), "audit");

        public OnlineApiEndpointProfile GetTelemetryEndpoint() => new(OnlineApiOperation.Telemetry, new Uri("https://custom.example/"), "telemetry");

        public OnlineApiEndpointProfile GetGitHubRepositoryEndpoint() => new(OnlineApiOperation.GitHubRepositoryMetadata, new Uri("https://custom.example/"), "repo");

        public OnlineApiEndpointProfile GetGitHubTreeEndpoint() => new(OnlineApiOperation.GitHubTreeMetadata, new Uri("https://custom.example/"), "tree");

        public IReadOnlyList<WellKnownDiscoveryEndpointCandidate> GetWellKnownDiscoveryEndpoints(Uri sourceUri) =>
            [new WellKnownDiscoveryEndpointCandidate(new Uri(sourceUri, ".well-known/skills/index.json"), sourceUri)];
    }
}
