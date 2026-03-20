using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = HermesConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<HermesOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new HermesConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
