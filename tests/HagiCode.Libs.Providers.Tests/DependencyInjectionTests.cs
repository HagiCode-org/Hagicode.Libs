using Shouldly;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.QoderCli;
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
        var hermesProvider = serviceProvider.GetRequiredService<ICliProvider<HermesOptions>>();
        var qoderCliProvider = serviceProvider.GetRequiredService<ICliProvider<QoderCliOptions>>();
        var allProviders = serviceProvider.GetServices<ICliProvider>().ToArray();

        registry.GetProvider("claude-code").ShouldNotBeNull();
        registry.GetProvider("codebuddy").ShouldNotBeNull();
        registry.GetProvider("codex").ShouldNotBeNull();
        registry.GetProvider("hermes").ShouldNotBeNull();
        registry.GetProvider("hermes-cli").ShouldNotBeNull();
        registry.GetProvider("qodercli").ShouldNotBeNull();
        claudeProvider.ShouldBeOfType<ClaudeCodeProvider>();
        codebuddyProvider.ShouldBeOfType<CodebuddyProvider>();
        codexProvider.ShouldBeOfType<CodexProvider>();
        hermesProvider.ShouldBeOfType<HermesProvider>();
        qoderCliProvider.ShouldBeOfType<QoderCliProvider>();
        allProviders.ShouldContain(provider => provider is HermesProvider);
        registry.GetProvider<HermesOptions>("hermes").ShouldBeOfType<HermesProvider>();
        registry.GetProvider<HermesOptions>("hermes-cli").ShouldBeOfType<HermesProvider>();
        registry.GetProvider<QoderCliOptions>("qodercli").ShouldBeOfType<QoderCliProvider>();
        registry.GetAllProviders().Select(static provider => provider.Name).ShouldBe(["claude-code", "codebuddy", "codex", "hermes", "qodercli"], ignoreOrder: true);
    }
}
