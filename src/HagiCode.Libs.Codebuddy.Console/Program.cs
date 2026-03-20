using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Codebuddy;

namespace HagiCode.Libs.Codebuddy.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = CodebuddyConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<CodebuddyOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new CodebuddyConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
