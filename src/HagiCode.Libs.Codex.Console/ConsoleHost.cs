using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;
using Microsoft.Extensions.DependencyInjection;

namespace HagiCode.Libs.Codex.Console;

public static class ConsoleHost
{
    public static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddHagiCodeLibs();
        return services.BuildServiceProvider();
    }

    public static ICodexProvider GetProvider(ServiceProvider provider)
    {
        return provider.GetRequiredService<ICodexProvider>();
    }
}
