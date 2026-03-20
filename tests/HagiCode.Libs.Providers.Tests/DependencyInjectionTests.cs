using Shouldly;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Codex;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Providers.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task AddHagiCodeLibs_registers_provider_registry_and_all_builtin_providers()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();

        await using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<ProviderRegistry>();
        var claudeProvider = serviceProvider.GetRequiredService<ICliProvider<ClaudeCodeOptions>>();
        var codebuddyProvider = serviceProvider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
        var codexProvider = serviceProvider.GetRequiredService<ICliProvider<CodexOptions>>();

        registry.GetProvider("claude-code").ShouldNotBeNull();
        registry.GetProvider("codebuddy").ShouldNotBeNull();
        registry.GetProvider("codex").ShouldNotBeNull();
        claudeProvider.ShouldBeOfType<ClaudeCodeProvider>();
        codebuddyProvider.ShouldBeOfType<CodebuddyProvider>();
        codexProvider.ShouldBeOfType<CodexProvider>();
        registry.GetAllProviders().Select(static provider => provider.Name).ShouldBe(["claude-code", "codebuddy", "codex"], ignoreOrder: true);
    }
}
