using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.Options;
using Shouldly;

namespace HagiCode.Libs.Skills.Tests;

public sealed class VercelOnlineApiEndpointProviderTests
{
    [Fact]
    public void Uses_documented_default_hosts()
    {
        var provider = CreateProvider();

        provider.GetSearchEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://skills.sh/");
        provider.GetSearchEndpoint().RelativePathTemplate.ShouldBe("api/search");
        provider.GetAuditEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://add-skill.vercel.sh/");
        provider.GetTelemetryEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://add-skill.vercel.sh/");
        provider.GetGitHubRepositoryEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://api.github.com/");
        provider.GetGitHubTreeEndpoint().RelativePathTemplate.ShouldBe("repos/{owner}/{repo}/git/trees/{branch}");
    }

    [Fact]
    public void Uses_option_based_overrides_when_configured()
    {
        var provider = CreateProvider(options =>
        {
            options.SearchBaseUri = new Uri("https://search.example/");
            options.AuditBaseUri = new Uri("https://audit.example/");
            options.TelemetryBaseUri = new Uri("https://telemetry.example/");
            options.GitHubBaseUri = new Uri("https://github-proxy.example/");
            options.GitHubUserAgent = "skills-tests";
        });

        provider.GetSearchEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://search.example/");
        provider.GetAuditEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://audit.example/");
        provider.GetTelemetryEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://telemetry.example/");
        provider.GetGitHubRepositoryEndpoint().BaseUri.AbsoluteUri.ShouldBe("https://github-proxy.example/");
        provider.GetGitHubRepositoryEndpoint().DefaultHeaders!["User-Agent"].ShouldBe("skills-tests");
    }

    [Fact]
    public void Returns_path_relative_well_known_candidate_before_root_fallback()
    {
        var provider = CreateProvider();

        var candidates = provider.GetWellKnownDiscoveryEndpoints(new Uri("https://example.com/docs/reference"));

        candidates.Count.ShouldBe(2);
        candidates[0].IndexUri.AbsoluteUri.ShouldBe("https://example.com/docs/reference/.well-known/skills/index.json");
        candidates[0].ResolvedBaseUri.AbsoluteUri.ShouldBe("https://example.com/docs/reference/");
        candidates[1].IndexUri.AbsoluteUri.ShouldBe("https://example.com/.well-known/skills/index.json");
        candidates[1].ResolvedBaseUri.AbsoluteUri.ShouldBe("https://example.com/");
    }

    [Fact]
    public void Returns_only_root_well_known_candidate_for_root_urls()
    {
        var provider = CreateProvider();

        var candidates = provider.GetWellKnownDiscoveryEndpoints(new Uri("https://example.com/"));

        candidates.Count.ShouldBe(1);
        candidates[0].IndexUri.AbsoluteUri.ShouldBe("https://example.com/.well-known/skills/index.json");
    }

    private static VercelOnlineApiEndpointProvider CreateProvider(Action<OnlineApiOptions>? configure = null)
    {
        var options = new OnlineApiOptions();
        configure?.Invoke(options);
        return new VercelOnlineApiEndpointProvider(Options.Create(options));
    }
}
