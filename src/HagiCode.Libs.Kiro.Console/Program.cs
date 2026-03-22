using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Kiro;

namespace HagiCode.Libs.Kiro.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = KiroConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<KiroOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new KiroConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
