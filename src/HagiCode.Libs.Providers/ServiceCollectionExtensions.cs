using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Execution;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Gemini;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.Kimi;
using HagiCode.Libs.Providers.Kiro;
using HagiCode.Libs.Providers.OpenCode;
using HagiCode.Libs.Providers.Pooling;
using HagiCode.Libs.Providers.QoderCli;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Providers;

/// <summary>
/// Registers HagiCode library services into a dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the built-in HagiCode library services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddHagiCodeLibs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<CliExecutableResolver>();
        services.AddSingleton<CliProcessManager>();
        services.AddSingleton<IShellCommandRunner, ProcessShellCommandRunner>();
        services.AddSingleton<IRuntimeEnvironmentResolver, RuntimeEnvironmentResolver>();
        services.AddSingleton<ICliExecutionPolicy, AllowAllCliExecutionPolicy>();
        services.AddSingleton<ICliExecutionFacade, CliExecutionFacade>();
        services.AddSingleton<ICopilotSdkGateway, GitHubCopilotSdkGateway>();
        services.AddSingleton<ICliAcpSessionPool, CliAcpSessionPool>();
        services.AddSingleton<CliProviderPoolCoordinator>();
        services.AddSingleton(static _ =>
        {
            var registry = new CliProviderPoolConfigurationRegistry();
            registry.Register("claude-code", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(5) });
            registry.Register("codebuddy", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("copilot", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("codex", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("gemini", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("hermes", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("kimi", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("kiro", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            registry.Register("qodercli", new CliPoolSettings { MaxActiveSessions = 50, IdleTimeout = TimeSpan.FromMinutes(10) });
            return registry;
        });
        services.AddSingleton<ClaudeCodeProvider>();
        services.AddSingleton<CodebuddyProvider>();
        services.AddSingleton(serviceProvider => new CopilotProvider(
            serviceProvider.GetRequiredService<CliExecutableResolver>(),
            serviceProvider.GetRequiredService<CliProcessManager>(),
            serviceProvider.GetRequiredService<ICopilotSdkGateway>(),
            serviceProvider.GetRequiredService<IRuntimeEnvironmentResolver>()));
        services.AddSingleton<CodexProvider>();
        services.AddSingleton<GeminiProvider>();
        services.AddSingleton<HermesProvider>();
        services.AddSingleton<KimiProvider>();
        services.AddSingleton<KiroProvider>();
        services.AddSingleton<OpenCodeStandaloneServerHost>();
        services.AddSingleton<IOpenCodeStandaloneServerClient>(serviceProvider => serviceProvider.GetRequiredService<OpenCodeStandaloneServerHost>());
        services.AddSingleton<OpenCodeProvider>();
        services.AddSingleton<QoderCliProvider>();
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CodebuddyProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CopilotProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<HermesProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<KimiProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<KiroProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<OpenCodeProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<QoderCliProvider>());
        services.AddSingleton<ICliProvider<ClaudeCodeOptions>>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider<CodebuddyOptions>>(serviceProvider => serviceProvider.GetRequiredService<CodebuddyProvider>());
        services.AddSingleton<ICliProvider<CopilotOptions>>(serviceProvider => serviceProvider.GetRequiredService<CopilotProvider>());
        services.AddSingleton<ICliProvider<CodexOptions>>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton<ICliProvider<GeminiOptions>>(serviceProvider => serviceProvider.GetRequiredService<GeminiProvider>());
        services.AddSingleton<ICliProvider<HermesOptions>>(serviceProvider => serviceProvider.GetRequiredService<HermesProvider>());
        services.AddSingleton<ICliProvider<KimiOptions>>(serviceProvider => serviceProvider.GetRequiredService<KimiProvider>());
        services.AddSingleton<ICliProvider<KiroOptions>>(serviceProvider => serviceProvider.GetRequiredService<KiroProvider>());
        services.AddSingleton<ICliProvider<OpenCodeOptions>>(serviceProvider => serviceProvider.GetRequiredService<OpenCodeProvider>());
        services.AddSingleton<ICliProvider<QoderCliOptions>>(serviceProvider => serviceProvider.GetRequiredService<QoderCliProvider>());
        services.AddSingleton(static serviceProvider =>
        {
            var registry = new ProviderRegistry();
            foreach (var provider in serviceProvider.GetServices<ICliProvider>())
            {
                if (provider is GeminiProvider)
                {
                    registry.Register(provider.Name, provider, ["gemini-cli"]);
                    continue;
                }

                if (provider is HermesProvider)
                {
                    registry.Register(provider.Name, provider, ["hermes-cli"]);
                    continue;
                }

                if (provider is ClaudeCodeProvider)
                {
                    registry.Register(provider.Name, provider, ["claude", "claudecode", "anthropic-claude"]);
                    continue;
                }

                if (provider is CodebuddyProvider)
                {
                    registry.Register(provider.Name, provider, ["codebuddy-cli"]);
                    continue;
                }

                if (provider is CopilotProvider)
                {
                    registry.Register(provider.Name, provider, ["github-copilot", "githubcopilot"]);
                    continue;
                }

                if (provider is KimiProvider)
                {
                    registry.Register(provider.Name, provider, ["kimi-cli"]);
                    continue;
                }

                if (provider is KiroProvider)
                {
                    registry.Register(provider.Name, provider, ["kiro-cli"]);
                    continue;
                }

                if (provider is OpenCodeProvider)
                {
                    registry.Register(provider.Name, provider, ["open-code", "opencode-cli"]);
                    continue;
                }

                registry.Register(provider.Name, provider);
            }

            return registry;
        });

        return services;
    }
}
