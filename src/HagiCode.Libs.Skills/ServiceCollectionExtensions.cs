using HagiCode.Libs.Skills.OnlineApi;
using HagiCode.Libs.Skills.OnlineApi.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HagiCode.Libs.Skills;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHagiCodeSkills(
        this IServiceCollection services,
        Action<OnlineApiOptions>? configureOnlineApi = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OnlineApiOptions>();
        if (configureOnlineApi is not null)
        {
            services.Configure(configureOnlineApi);
        }

        services.TryAddSingleton<IOnlineApiEndpointProvider, VercelOnlineApiEndpointProvider>();
        services.AddHttpClient<IOnlineApiClient, OnlineApiClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OnlineApiOptions>>().Value;
            client.Timeout = options.RequestTimeout;
        });

        return services;
    }
}
