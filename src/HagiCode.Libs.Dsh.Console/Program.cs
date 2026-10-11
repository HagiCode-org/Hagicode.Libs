using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = DshConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<DshOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new DshConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
