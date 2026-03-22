using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Kimi;

namespace HagiCode.Libs.Kimi.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = KimiConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<KimiOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new KimiConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
