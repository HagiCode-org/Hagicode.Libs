using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = DeepAgentsConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<DeepAgentsOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new DeepAgentsConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
