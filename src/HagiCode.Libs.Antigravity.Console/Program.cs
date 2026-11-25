using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Antigravity;

namespace HagiCode.Libs.Antigravity.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = AntigravityConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<AntigravityOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new AntigravityConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
