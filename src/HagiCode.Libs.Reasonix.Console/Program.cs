using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Reasonix;

namespace HagiCode.Libs.Reasonix.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = ReasonixConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<ReasonixOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new ReasonixConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
