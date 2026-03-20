using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.IFlow;

namespace HagiCode.Libs.IFlow.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = IFlowConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<IFlowOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new IFlowConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
