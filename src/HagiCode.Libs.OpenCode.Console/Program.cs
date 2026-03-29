using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.OpenCode;

namespace HagiCode.Libs.OpenCode.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = OpenCodeConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<OpenCodeOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new OpenCodeConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
