using HagiCode.Libs.Skills;
using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class RealOnlineApiIntegrationTests
{
    private const string RealOnlineApiTestsEnvironmentVariable = "HAGICODE_REAL_ONLINE_API_TESTS";

    [Fact]
    [Trait("Category", "RealOnlineApi")]
    public async Task SearchAsync_can_query_the_real_search_api_when_opted_in()
    {
        if (!IsRealOnlineApiTestsEnabled())
        {
            return;
        }

        using var serviceProvider = CreateServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();

        var response = await client.SearchAsync(new SearchSkillsRequest
        {
            Query = "codex",
            Limit = 3,
        });

        response.Skills.Count.ShouldBeGreaterThan(0);
        response.Skills.ShouldAllBe(static skill =>
            !string.IsNullOrWhiteSpace(skill.Id) &&
            !string.IsNullOrWhiteSpace(skill.Name));
    }

    [Fact]
    [Trait("Category", "RealOnlineApi")]
    public async Task DiscoverWellKnownAsync_can_fallback_to_the_real_root_index_when_opted_in()
    {
        if (!IsRealOnlineApiTestsEnabled())
        {
            return;
        }

        using var serviceProvider = CreateServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();

        var response = await client.DiscoverWellKnownAsync(new WellKnownDiscoveryRequest
        {
            SourceUrl = "https://transloadit.com/docs",
        });

        response.ResolvedIndexUri.AbsoluteUri.ShouldBe("https://transloadit.com/.well-known/skills/index.json");
        response.Skills.Count.ShouldBeGreaterThan(0);
        response.Skills.ShouldAllBe(static skill =>
            skill.Files.Contains("SKILL.md", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "RealOnlineApi")]
    public async Task GetAuditMetadataAsync_can_call_the_real_audit_api_when_opted_in()
    {
        if (!IsRealOnlineApiTestsEnabled())
        {
            return;
        }

        using var serviceProvider = CreateServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();

        var response = await client.GetAuditMetadataAsync(new AuditMetadataRequest
        {
            Source = "vercel-labs/agent-skills",
            Skills = ["find-skills"],
        });

        response.Sources.Count.ShouldBeGreaterThan(0);
        response.Sources.ShouldContainKey("find-skills");
    }

    [Fact]
    [Trait("Category", "RealOnlineApi")]
    public async Task TrackTelemetryAsync_can_reach_the_real_telemetry_endpoint_when_opted_in()
    {
        if (!IsRealOnlineApiTestsEnabled())
        {
            return;
        }

        using var serviceProvider = CreateServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();

        var result = await client.TrackTelemetryAsync(new FindTelemetryEventRequest(
            Query: "hagicode-libs-real-online-api-test",
            ResultCount: "1",
            Interactive: false));

        result.IsSkipped.ShouldBeFalse();
        result.RequestUri.Host.ShouldBe("add-skill.vercel.sh");
    }

    [Fact]
    [Trait("Category", "RealOnlineApi")]
    public async Task GitHub_operations_can_query_the_real_github_api_when_opted_in()
    {
        if (!IsRealOnlineApiTestsEnabled())
        {
            return;
        }

        using var serviceProvider = CreateServiceProvider();
        var client = serviceProvider.GetRequiredService<IOnlineApiClient>();

        var repository = await client.GetGitHubRepositoryMetadataAsync(new GitHubRepositoryMetadataRequest
        {
            Owner = "vercel-labs",
            Repository = "agent-skills",
        });
        var tree = await client.GetGitHubTreeMetadataAsync(new GitHubTreeMetadataRequest
        {
            Owner = "vercel-labs",
            Repository = "agent-skills",
            Branch = repository.DefaultBranch,
        });

        repository.FullName.ShouldBe("vercel-labs/agent-skills");
        repository.Private.ShouldBeFalse();
        tree.Sha.ShouldNotBeNullOrWhiteSpace();
        tree.Tree.Count.ShouldBeGreaterThan(0);
    }

    private static bool IsRealOnlineApiTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealOnlineApiTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeSkills(options =>
        {
            options.RequestTimeout = TimeSpan.FromSeconds(20);
            options.TelemetryVersion = "hagicode-libs-real-online-api-tests";
            options.TelemetryIsCi = true;
            options.GitHubUserAgent = "HagiCode.Libs.Skills.RealOnlineApiTests";
        });

        return services.BuildServiceProvider();
    }
}
