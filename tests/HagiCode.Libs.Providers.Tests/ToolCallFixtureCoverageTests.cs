using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ToolCallFixtureCoverageTests
{
    [Test]
    public async Task Every_registry_provider_that_emits_tool_calls_has_a_toolcalls_fixture()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();

        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<ProviderRegistry>();

        // Enumerate the registry, keep the providers that emit tool calls, and require a fixture directory for each.
        var emitters = registry.GetAllProviders()
            .Select(static provider => provider.Name)
            .Where(static name => ToolCallFixtureCatalog.ToolCallEmittingProviders.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        emitters.Length.ShouldBe(
            ToolCallFixtureCatalog.ToolCallEmittingProviders.Count,
            "every provider that emits tool calls must be a registered provider name");

        var missing = emitters.Where(static name => !ToolCallFixtureFile.HasProviderDirectory(name)).ToArray();
        missing.ShouldBeEmpty("providers that emit tool calls need a Fixtures/ToolCalls/<provider>/ entry");

        var orphans = ToolCallFixtureFile.ProviderDirectoryNames()
            .Where(name => !ToolCallFixtureCatalog.ToolCallEmittingProviders.Contains(name))
            .ToArray();
        orphans.ShouldBeEmpty("a Fixtures/ToolCalls/<provider>/ directory must belong to a provider that emits tool calls");
    }
}
