using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codebuddy;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Hermes;
using HagiCode.Libs.Providers.IFlow;
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
        services.AddSingleton<IIFlowAcpBootstrapper, IFlowProcessBootstrapper>();
        services.AddSingleton<ClaudeCodeProvider>();
        services.AddSingleton<CodebuddyProvider>();
        services.AddSingleton<CodexProvider>();
        services.AddSingleton<HermesProvider>();
        services.AddSingleton<IFlowProvider>();
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CodebuddyProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<HermesProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<IFlowProvider>());
        services.AddSingleton<ICliProvider<ClaudeCodeOptions>>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider<CodebuddyOptions>>(serviceProvider => serviceProvider.GetRequiredService<CodebuddyProvider>());
        services.AddSingleton<ICliProvider<CodexOptions>>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton<ICliProvider<HermesOptions>>(serviceProvider => serviceProvider.GetRequiredService<HermesProvider>());
        services.AddSingleton<ICliProvider<IFlowOptions>>(serviceProvider => serviceProvider.GetRequiredService<IFlowProvider>());
        services.AddSingleton(static serviceProvider =>
        {
            var registry = new ProviderRegistry();
            foreach (var provider in serviceProvider.GetServices<ICliProvider>())
            {
                if (provider is HermesProvider)
                {
                    registry.Register(provider.Name, provider, ["hermes-cli"]);
                    continue;
                }

                registry.Register(provider.Name, provider);
            }

            return registry;
        });

        return services;
    }
}
