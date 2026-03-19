using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Codex.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = CodexConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<CodexOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new CodexConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
