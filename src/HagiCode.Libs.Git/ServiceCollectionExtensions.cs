using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HagiCode.Libs.Git;

/// <summary>Dependency-injection registration for the git wrapper.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGitClient" /> as a singleton backed by <see cref="GitClient" />, with options configured by
    /// <paramref name="configure" />. Calling it again replaces the earlier registration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration for <see cref="GitClientOptions" />.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddHagiCodeGit(this IServiceCollection services, Action<GitClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new GitClientOptions();
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton<IGitClient>(_ => new GitClient(options)));
        return services;
    }
}
