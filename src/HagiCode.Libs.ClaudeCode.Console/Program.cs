using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = ClaudeConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<ClaudeCodeOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new ClaudeConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
