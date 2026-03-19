using FluentAssertions;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddHagiCodeLibs_registers_provider_registry_and_claude_provider()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();

        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<ProviderRegistry>();
        var provider = serviceProvider.GetRequiredService<ICliProvider<ClaudeCodeOptions>>();

        registry.GetProvider("claude-code").Should().NotBeNull();
        provider.Should().BeOfType<ClaudeCodeProvider>();
    }
}
