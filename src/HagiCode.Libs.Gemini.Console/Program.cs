using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers.Gemini;

namespace HagiCode.Libs.Gemini.Console;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = GeminiConsoleDefinition.Instance;

        await using var services = ConsoleHost.BuildServiceProvider();
        var provider = ConsoleHost.GetProvider<GeminiOptions>(services);
        var formatter = new ProviderConsoleOutputFormatter();
        var runner = new GeminiConsoleRunner(definition, provider, formatter);

        return await ProviderConsoleCommandDispatcher.DispatchAsync(args, definition, runner);
    }
}
