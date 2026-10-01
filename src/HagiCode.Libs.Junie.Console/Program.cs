using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Junie;

namespace HagiCode.Libs.Junie.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = JunieConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<JunieOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new JunieConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
