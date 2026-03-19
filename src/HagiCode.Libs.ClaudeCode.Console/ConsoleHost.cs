using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.ClaudeCode.Console;

public static class ConsoleHost
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();
        return services.BuildServiceProvider();
    }

    public static ICliProvider<TOptions> GetProvider<TOptions>(ServiceProvider provider)
        where TOptions : class
    {
        return provider.GetRequiredService<ICliProvider<TOptions>>();
    }
}
