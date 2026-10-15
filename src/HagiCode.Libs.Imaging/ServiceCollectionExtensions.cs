using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Imaging;

/// <summary>Dependency injection registration for <see cref="IAvifConverter"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAvifConverter"/> as a singleton and applies the configured limits. The
    /// <see cref="AvifConverterOptions"/> instance is also registered so hosts can read the effective limits.
    /// </summary>
    public static IServiceCollection AddHagiCodeImaging(
        this IServiceCollection services,
        Action<AvifConverterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AvifConverterOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IAvifConverter>(_ => new AvifConverter(options.Snapshot()));
        return services;
    }
}
