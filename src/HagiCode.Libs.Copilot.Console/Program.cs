using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Copilot.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = CopilotConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<CopilotOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new CopilotConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
