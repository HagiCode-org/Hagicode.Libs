using HagiCode.Libs.Prompts.Configuration;
using HagiCode.Libs.Prompts.FileSystem;
using HagiCode.Libs.Prompts.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Prompts;

/// <summary>
/// Registers prompt management services in a dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HagiCode prompt management services.
    /// </summary>
    public static IServiceCollection AddHagiCodePrompts(
        this IServiceCollection services,
        Action<PromptManagementOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<PromptManagementOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IPromptRenderer, HandlebarsPromptRenderer>();
        services.AddSingleton<FilePromptCatalog>();
        services.AddSingleton<IPromptCatalog>(static serviceProvider => serviceProvider.GetRequiredService<FilePromptCatalog>());
        services.AddSingleton<IPromptDiagnosticsService>(static serviceProvider => serviceProvider.GetRequiredService<FilePromptCatalog>());

        return services;
    }
}
