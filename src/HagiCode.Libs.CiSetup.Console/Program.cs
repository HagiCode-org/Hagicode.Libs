namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// Entry point for the CI setup console.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the console.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var environment = CiSetupEnvironment.CreateDefault(System.Console.Out, System.Console.Error);
        return await new CiSetupRunner(environment).RunAsync(args).ConfigureAwait(false);
    }
}
