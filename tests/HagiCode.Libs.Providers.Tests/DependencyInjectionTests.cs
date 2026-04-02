using Shouldly;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.DeepAgents;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.OpenCode;
using HagiCode.Libs.Providers.Pooling;
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
        var executionFacade = serviceProvider.GetRequiredService<ICliExecutionFacade>();
        var acpPool = serviceProvider.GetRequiredService<ICliAcpSessionPool>();
        var poolCoordinator = serviceProvider.GetRequiredService<CliProviderPoolCoordinator>();
        var poolConfiguration = serviceProvider.GetRequiredService<CliProviderPoolConfigurationRegistry>();
        var claudeProvider = serviceProvider.GetRequiredService<ICliProvider<ClaudeCodeOptions>>();
        var codebuddyProvider = serviceProvider.GetRequiredService<ICliProvider<CodebuddyOptions>>();
        var copilotProvider = serviceProvider.GetRequiredService<ICliProvider<CopilotOptions>>();
        var codexProvider = serviceProvider.GetRequiredService<ICliProvider<CodexOptions>>();
        var deepAgentsProvider = serviceProvider.GetRequiredService<ICliProvider<DeepAgentsOptions>>();
        var geminiProvider = serviceProvider.GetRequiredService<ICliProvider<GeminiOptions>>();
        var hermesProvider = serviceProvider.GetRequiredService<ICliProvider<HermesOptions>>();
        var kimiProvider = serviceProvider.GetRequiredService<ICliProvider<KimiOptions>>();
        var kiroProvider = serviceProvider.GetRequiredService<ICliProvider<KiroOptions>>();
        var openCodeProvider = serviceProvider.GetRequiredService<ICliProvider<OpenCodeOptions>>();
        var qoderCliProvider = serviceProvider.GetRequiredService<ICliProvider<QoderCliOptions>>();
        var allProviders = serviceProvider.GetServices<ICliProvider>().ToArray();

        executionFacade.ShouldNotBeNull();
        acpPool.ShouldNotBeNull();
        poolCoordinator.ShouldNotBeNull();
        poolConfiguration.GetSettings("hermes").Enabled.ShouldBeTrue();
        registry.GetProvider("claude-code").ShouldNotBeNull();
        registry.GetProvider("claude").ShouldNotBeNull();
        registry.GetProvider("claudecode").ShouldNotBeNull();
        registry.GetProvider("codebuddy").ShouldNotBeNull();
        registry.GetProvider("codebuddy-cli").ShouldNotBeNull();
        registry.GetProvider("copilot").ShouldNotBeNull();
        registry.GetProvider("github-copilot").ShouldNotBeNull();
        registry.GetProvider("githubcopilot").ShouldNotBeNull();
        registry.GetProvider("codex").ShouldNotBeNull();
        registry.GetProvider("deepagents").ShouldNotBeNull();
        registry.GetProvider("deepagents-acp").ShouldBeNull();
        registry.GetProvider("gemini").ShouldNotBeNull();
        registry.GetProvider("gemini-cli").ShouldNotBeNull();
        registry.GetProvider("hermes").ShouldNotBeNull();
        registry.GetProvider("hermes-cli").ShouldNotBeNull();
        registry.GetProvider("kimi").ShouldNotBeNull();
        registry.GetProvider("kimi-cli").ShouldNotBeNull();
        registry.GetProvider("kiro").ShouldNotBeNull();
        registry.GetProvider("kiro-cli").ShouldNotBeNull();
        registry.GetProvider("opencode").ShouldNotBeNull();
        registry.GetProvider("open-code").ShouldNotBeNull();
        registry.GetProvider("opencode-cli").ShouldNotBeNull();
        registry.GetProvider("qodercli").ShouldNotBeNull();
        claudeProvider.ShouldBeOfType<ClaudeCodeProvider>();
        codebuddyProvider.ShouldBeOfType<CodebuddyProvider>();
        copilotProvider.ShouldBeOfType<CopilotProvider>();
        codexProvider.ShouldBeOfType<CodexProvider>();
        deepAgentsProvider.ShouldBeOfType<DeepAgentsProvider>();
        geminiProvider.ShouldBeOfType<GeminiProvider>();
        hermesProvider.ShouldBeOfType<HermesProvider>();
        kimiProvider.ShouldBeOfType<KimiProvider>();
        kiroProvider.ShouldBeOfType<KiroProvider>();
        openCodeProvider.ShouldBeOfType<OpenCodeProvider>();
        qoderCliProvider.ShouldBeOfType<QoderCliProvider>();
        allProviders.ShouldContain(provider => provider is GeminiProvider);
        allProviders.ShouldContain(provider => provider is HermesProvider);
        allProviders.ShouldContain(provider => provider is KimiProvider);
        allProviders.ShouldContain(provider => provider is KiroProvider);
        allProviders.ShouldContain(provider => provider is OpenCodeProvider);
        allProviders.ShouldContain(provider => provider is DeepAgentsProvider);
        registry.GetProvider<CopilotOptions>("copilot").ShouldBeOfType<CopilotProvider>();
        registry.GetProvider<CopilotOptions>("github-copilot").ShouldBeOfType<CopilotProvider>();
        registry.GetProvider<CopilotOptions>("githubcopilot").ShouldBeOfType<CopilotProvider>();
        registry.GetProvider<ClaudeCodeOptions>("claude").ShouldBeOfType<ClaudeCodeProvider>();
        registry.GetProvider<ClaudeCodeOptions>("claudecode").ShouldBeOfType<ClaudeCodeProvider>();
        registry.GetProvider<CodebuddyOptions>("codebuddy-cli").ShouldBeOfType<CodebuddyProvider>();
        registry.GetProvider<DeepAgentsOptions>("deepagents").ShouldBeOfType<DeepAgentsProvider>();
        registry.GetProvider<DeepAgentsOptions>("deepagents-acp").ShouldBeNull();
        registry.GetProvider<GeminiOptions>("gemini").ShouldBeOfType<GeminiProvider>();
        registry.GetProvider<GeminiOptions>("gemini-cli").ShouldBeOfType<GeminiProvider>();
        registry.GetProvider<HermesOptions>("hermes").ShouldBeOfType<HermesProvider>();
        registry.GetProvider<HermesOptions>("hermes-cli").ShouldBeOfType<HermesProvider>();
        registry.GetProvider<KimiOptions>("kimi").ShouldBeOfType<KimiProvider>();
        registry.GetProvider<KimiOptions>("kimi-cli").ShouldBeOfType<KimiProvider>();
        registry.GetProvider<KiroOptions>("kiro").ShouldBeOfType<KiroProvider>();
        registry.GetProvider<KiroOptions>("kiro-cli").ShouldBeOfType<KiroProvider>();
        registry.GetProvider<OpenCodeOptions>("opencode").ShouldBeOfType<OpenCodeProvider>();
        registry.GetProvider<OpenCodeOptions>("open-code").ShouldBeOfType<OpenCodeProvider>();
        registry.GetProvider<OpenCodeOptions>("opencode-cli").ShouldBeOfType<OpenCodeProvider>();
        registry.GetProvider<QoderCliOptions>("qodercli").ShouldBeOfType<QoderCliProvider>();
        registry.GetAllProviders().Select(static provider => provider.Name).ShouldBe(["claude-code", "codebuddy", "copilot", "codex", "deepagents", "gemini", "hermes", "kimi", "kiro", "opencode", "qodercli"], ignoreOrder: true);
    }
}
