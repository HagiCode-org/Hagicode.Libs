using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.QoderCli;

namespace HagiCode.Libs.QoderCli.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = QoderCliConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<QoderCliOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new QoderCliConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
