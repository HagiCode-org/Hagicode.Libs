using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.Providers.Codex;
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
        services.AddSingleton<ClaudeCodeProvider>();
        services.AddSingleton<CodexProvider>();
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton<ICliProvider<ClaudeCodeOptions>>(serviceProvider => serviceProvider.GetRequiredService<ClaudeCodeProvider>());
        services.AddSingleton<ICliProvider<CodexOptions>>(serviceProvider => serviceProvider.GetRequiredService<CodexProvider>());
        services.AddSingleton(static serviceProvider =>
        {
            var registry = new ProviderRegistry();
            foreach (var provider in serviceProvider.GetServices<ICliProvider>())
            {
                registry.Register(provider.Name, provider);
            }

            return registry;
        });

        return services;
    }
}
