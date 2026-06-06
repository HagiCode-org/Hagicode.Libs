using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = PiConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<PiOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new PiConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
